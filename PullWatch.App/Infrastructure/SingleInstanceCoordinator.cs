using System.IO.Pipes;

namespace PullWatch;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
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

    public void StartActivationListener(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);

        if (_mutex is null)
        {
            throw new InvalidOperationException("The single-instance lock has not been acquired.");
        }

        _listenerTask ??= ListenAsync(activate, _cancellation.Token);
    }

    public async Task<bool> ActivateExistingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous
            );
            await client.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
            when (exception
                    is System.IO.IOException
                        or TimeoutException
                        or OperationCanceledException
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

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[1];
                await server.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                activate();
            }
            catch (System.IO.IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client disconnected before sending a complete activation request.
            }
        }
    }
}
