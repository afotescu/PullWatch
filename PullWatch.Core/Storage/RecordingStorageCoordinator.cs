using Microsoft.Extensions.Logging;

namespace PullWatch;

internal sealed class RecordingStorageCoordinator : IAsyncDisposable
{
    private readonly Func<
        PullWatchSettings,
        CancellationToken,
        Task<RecordingStorageUsage>
    >? _getUsage;
    private readonly Func<
        PullWatchSettings,
        CancellationToken,
        Task<RecordingStorageCleanupResult>
    >? _enforceLimit;
    private readonly ILogger<RecordingStorageCoordinator> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _operationSync = new();
    private RecordingStorageStatus _status = RecordingStorageStatus.Initial;
    private TaskCompletionSource? _operationsDrained;
    private int _operationVersion;
    private int _activeOperationCount;
    private bool _disposed;

    public RecordingStorageCoordinator(
        RecordingStorageRetentionService? retention,
        ILogger<RecordingStorageCoordinator> logger
    )
    {
        if (retention is not null)
        {
            _getUsage = retention.GetUsageAsync;
            _enforceLimit = retention.EnforceLimitAsync;
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal RecordingStorageCoordinator(
        Func<PullWatchSettings, CancellationToken, Task<RecordingStorageUsage>>? getUsage,
        Func<
            PullWatchSettings,
            CancellationToken,
            Task<RecordingStorageCleanupResult>
        >? enforceLimit,
        ILogger<RecordingStorageCoordinator> logger
    )
    {
        _getUsage = getUsage;
        _enforceLimit = enforceLimit;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action<RecordingStorageStatus>? StatusChanged;

    public RecordingStorageStatus Status => Volatile.Read(ref _status);

    public void Start(PullWatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SetStatus(
            RecordingStorageStatus.Initial with
            {
                MaxUsageBytes = settings.Storage.MaxUsageBytes,
            }
        );
        QueueRefreshOrRetention(settings);
    }

    public void QueueRefreshOrRetention(PullWatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        int operationVersion;
        CancellationToken cancellationToken;

        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }

            operationVersion = ++_operationVersion;
            cancellationToken = _shutdownCancellation.Token;
            _activeOperationCount++;
        }

        var operation = RunOperationAsync(settings, operationVersion, cancellationToken);
        _ = ObserveOperationAsync(operation);
    }

    public void ResetStatus()
    {
        SetStatus(RecordingStorageStatus.Initial);
    }

    public async ValueTask DisposeAsync()
    {
        Task operationsDrained;

        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operationsDrained =
                _activeOperationCount == 0
                    ? Task.CompletedTask
                    : (
                        _operationsDrained ??= new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )
                    ).Task;
        }

        _shutdownCancellation.Cancel();
        await operationsDrained;
        _operationLock.Dispose();
        _shutdownCancellation.Dispose();
    }

    private async Task RunOperationAsync(
        PullWatchSettings settings,
        int operationVersion,
        CancellationToken cancellationToken
    )
    {
        if (_getUsage is null || _enforceLimit is null)
        {
            if (operationVersion == Volatile.Read(ref _operationVersion))
            {
                SetStatus(
                    RecordingStorageStatus.Initial with
                    {
                        MaxUsageBytes = settings.Storage.MaxUsageBytes,
                    }
                );
            }

            return;
        }

        var lockEntered = false;

        try
        {
            await _operationLock.WaitAsync(cancellationToken);
            lockEntered = true;

            if (operationVersion != Volatile.Read(ref _operationVersion))
            {
                return;
            }

            var enforceLimit = settings.Storage.IsLimitEnabled;
            var currentStatus = Status;
            SetStatus(
                currentStatus with
                {
                    MaxUsageBytes = settings.Storage.MaxUsageBytes,
                    IsRefreshing = !enforceLimit,
                    IsCleaning = enforceLimit,
                    LastDeletedRecordingCount = 0,
                    LastError = null,
                }
            );

            if (enforceLimit)
            {
                var result = await _enforceLimit(settings, cancellationToken);
                SetStatus(
                    new RecordingStorageStatus(
                        result.Usage.UsageBytes,
                        settings.Storage.MaxUsageBytes,
                        result.Usage.RecordingCount,
                        IsRefreshing: false,
                        IsCleaning: false,
                        LastDeletedRecordingCount: result.DeletedCount,
                        LastError: result.Errors.FirstOrDefault()
                    )
                );
                return;
            }

            var usage = await _getUsage(settings, cancellationToken);
            SetStatus(
                new RecordingStorageStatus(
                    usage.UsageBytes,
                    settings.Storage.MaxUsageBytes,
                    usage.RecordingCount,
                    IsRefreshing: false,
                    IsCleaning: false,
                    LastDeletedRecordingCount: 0,
                    LastError: null
                )
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The storage coordinator is shutting down.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not refresh managed recording storage usage");
            SetStatus(
                Status with
                {
                    MaxUsageBytes = settings.Storage.MaxUsageBytes,
                    IsRefreshing = false,
                    IsCleaning = false,
                    LastDeletedRecordingCount = 0,
                    LastError = exception,
                }
            );
        }
        finally
        {
            if (lockEntered)
            {
                _operationLock.Release();
            }
        }
    }

    private async Task ObserveOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Recording storage operation failed unexpectedly");
        }
        finally
        {
            CompleteOperation();
        }
    }

    private void CompleteOperation()
    {
        TaskCompletionSource? operationsDrained = null;

        lock (_operationSync)
        {
            _activeOperationCount--;

            if (_activeOperationCount == 0)
            {
                operationsDrained = _operationsDrained;
                _operationsDrained = null;
            }
        }

        operationsDrained?.TrySetResult();
    }

    private void SetStatus(RecordingStorageStatus status)
    {
        Volatile.Write(ref _status, status);
        NotifyStatusChanged(status);
    }

    private void NotifyStatusChanged(RecordingStorageStatus status)
    {
        var handlers = StatusChanged;

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<RecordingStorageStatus>>())
        {
            try
            {
                handler(status);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Recording storage status subscriber failed");
            }
        }
    }
}
