using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PullWatch;

public sealed record SingleInstanceLaunchRequest(string? AppVersion, string? ExecutablePath);

public enum SingleInstanceActivationResult
{
    ActivatedExisting,
    UpgradeAccepted,
    UpgradeRejected,
    Unavailable,
}

internal sealed record SingleInstanceActivationResponse(SingleInstanceActivationResult Result);

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan ActivationConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationRequestReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationResponseReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int MaxActivationPayloadBytes = 4096;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private Mutex? _mutex;
    private Task? _listenerTask;

    public SingleInstanceCoordinator(string name)
    {
        _mutexName = $@"Local\{name}";
        _pipeName = name;
    }

    public bool TryAcquire()
    {
        if (_mutex is not null)
        {
            return true;
        }

        var mutex = new Mutex(true, _mutexName, out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public void StartActivationListener(
        Func<
            SingleInstanceLaunchRequest,
            CancellationToken,
            Task<SingleInstanceActivationResult>
        > handleActivation,
        Action<SingleInstanceActivationResult, bool>? activationExchangeCompleted = null
    )
    {
        ArgumentNullException.ThrowIfNull(handleActivation);

        if (_mutex is null)
        {
            throw new InvalidOperationException("The single-instance lock has not been acquired.");
        }

        _listenerTask ??= ListenAsync(
            handleActivation,
            activationExchangeCompleted,
            _cancellation.Token
        );
    }

    public async Task<SingleInstanceActivationResult> ActivateExistingAsync(
        SingleInstanceLaunchRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await TryActivateWithProtocolAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result is not null)
        {
            return result.Value;
        }

        return await TryActivateWithLegacySignalAsync(cancellationToken).ConfigureAwait(false)
            ? SingleInstanceActivationResult.ActivatedExisting
            : SingleInstanceActivationResult.Unavailable;
    }

    public async Task<bool> WaitForReleaseAndAcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCancellation.CancelAfter(timeout);

        while (true)
        {
            if (TryAcquire())
            {
                return true;
            }

            try
            {
                await Task.Delay(LockRetryDelay, timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public Task<SingleInstanceActivationResult> ActivateExistingAsync(
        CancellationToken cancellationToken
    )
    {
        return ActivateExistingAsync(
            new SingleInstanceLaunchRequest(null, null),
            cancellationToken
        );
    }

    private async Task<SingleInstanceActivationResult?> TryActivateWithProtocolAsync(
        SingleInstanceLaunchRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            await client
                .ConnectAsync(ActivationConnectTimeout, cancellationToken)
                .ConfigureAwait(false);

            var requestJson = JsonSerializer.Serialize(request);
            await WriteLineAsync(client, requestJson, cancellationToken).ConfigureAwait(false);

            var responseJson = await ReadLineAsync(
                    client,
                    ActivationResponseReadTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var response = string.IsNullOrWhiteSpace(responseJson)
                ? null
                : JsonSerializer.Deserialize<SingleInstanceActivationResponse>(responseJson);

            return response?.Result;
        }
        catch (Exception exception)
            when (exception
                    is System.IO.IOException
                        or TimeoutException
                        or OperationCanceledException
                        or UnauthorizedAccessException
                        or JsonException
                        or NotSupportedException
            )
        {
            return null;
        }
    }

    private async Task<bool> TryActivateWithLegacySignalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous
            );
            await client
                .ConnectAsync(ActivationConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
            when (exception
                    is System.IO.IOException
                        or TimeoutException
                        or OperationCanceledException
                        or UnauthorizedAccessException
            )
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        _mutex?.Dispose();
        _cancellation.Dispose();
    }

    private async Task ListenAsync(
        Func<
            SingleInstanceLaunchRequest,
            CancellationToken,
            Task<SingleInstanceActivationResult>
        > handleActivation,
        Action<SingleInstanceActivationResult, bool>? activationExchangeCompleted,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SingleInstanceActivationResult? result = null;
            var responseSent = false;

            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var requestJson = await ReadLineAsync(
                        server,
                        ActivationRequestReadTimeout,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (requestJson is null)
                {
                    continue;
                }

                var request = TryDeserializeRequest(requestJson);
                var activationResult = await HandleActivationAsync(
                        handleActivation,
                        request,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                result = activationResult;
                var responseJson = JsonSerializer.Serialize(
                    new SingleInstanceActivationResponse(activationResult)
                );
                await WriteLineAsync(server, responseJson, cancellationToken).ConfigureAwait(false);
                responseSent = true;
            }
            catch (System.IO.IOException)
            {
                // A client disconnected before completing the activation exchange.
            }
            finally
            {
                if (result is { } completedResult)
                {
                    NotifyActivationExchangeCompleted(
                        activationExchangeCompleted,
                        completedResult,
                        responseSent
                    );
                }
            }
        }
    }

    private static void NotifyActivationExchangeCompleted(
        Action<SingleInstanceActivationResult, bool>? activationExchangeCompleted,
        SingleInstanceActivationResult result,
        bool responseSent
    )
    {
        try
        {
            activationExchangeCompleted?.Invoke(result, responseSent);
        }
        catch
        {
            // Listener callbacks must not stop future activation requests.
        }
    }

    private static async Task<string?> ReadLineAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCancellation.CancelAfter(timeout);

        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (bytes.Count < MaxActivationPayloadBytes)
        {
            int read;

            try
            {
                read = await stream
                    .ReadAsync(buffer, timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            }

            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                break;
            }

            if (buffer[0] != (byte)'\r')
            {
                bytes.Add(buffer[0]);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken
    )
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SingleInstanceLaunchRequest TryDeserializeRequest(string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return new SingleInstanceLaunchRequest(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<SingleInstanceLaunchRequest>(requestJson)
                ?? new SingleInstanceLaunchRequest(null, null);
        }
        catch (JsonException)
        {
            return new SingleInstanceLaunchRequest(null, null);
        }
    }

    private static async Task<SingleInstanceActivationResult> HandleActivationAsync(
        Func<
            SingleInstanceLaunchRequest,
            CancellationToken,
            Task<SingleInstanceActivationResult>
        > handleActivation,
        SingleInstanceLaunchRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await handleActivation(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested
            )
        {
            return SingleInstanceActivationResult.UpgradeRejected;
        }
    }
}
