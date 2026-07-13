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
    private int _refreshOrRetentionVersion;
    private int _usageRefreshVersion;
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
        QueueOperation(settings, allowRetention: true);
    }

    public void QueueUsageRefresh(PullWatchSettings settings)
    {
        QueueOperation(settings, allowRetention: false);
    }

    public async Task<T> ExecuteExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        CancellationToken shutdownCancellationToken;
        lock (_operationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            shutdownCancellationToken = _shutdownCancellation.Token;
            _activeOperationCount++;
        }

        var lockEntered = false;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            shutdownCancellationToken
        );

        try
        {
            await _operationLock.WaitAsync(linkedCancellation.Token);
            lockEntered = true;
            return await operation(linkedCancellation.Token);
        }
        finally
        {
            if (lockEntered)
            {
                _operationLock.Release();
            }

            CompleteOperation();
        }
    }

    public Task ExecuteExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteExclusiveAsync<bool>(
            async operationCancellationToken =>
            {
                await operation(operationCancellationToken);
                return true;
            },
            cancellationToken
        );
    }

    private void QueueOperation(PullWatchSettings settings, bool allowRetention)
    {
        ArgumentNullException.ThrowIfNull(settings);

        int refreshOrRetentionVersion;
        int usageRefreshVersion;
        CancellationToken cancellationToken;

        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }

            if (allowRetention)
            {
                refreshOrRetentionVersion = ++_refreshOrRetentionVersion;
                usageRefreshVersion = _usageRefreshVersion;
            }
            else
            {
                refreshOrRetentionVersion = _refreshOrRetentionVersion;
                usageRefreshVersion = ++_usageRefreshVersion;
            }

            cancellationToken = _shutdownCancellation.Token;
            _activeOperationCount++;
        }

        var operation = RunOperationAsync(
            settings,
            refreshOrRetentionVersion,
            usageRefreshVersion,
            allowRetention,
            cancellationToken
        );
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
        int refreshOrRetentionVersion,
        int usageRefreshVersion,
        bool allowRetention,
        CancellationToken cancellationToken
    )
    {
        if (_getUsage is null || _enforceLimit is null)
        {
            if (IsCurrentOperation(refreshOrRetentionVersion, usageRefreshVersion, allowRetention))
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

            if (!IsCurrentOperation(refreshOrRetentionVersion, usageRefreshVersion, allowRetention))
            {
                return;
            }

            var enforceLimit = allowRetention && settings.Storage.IsLimitEnabled;
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
                    CreateCompletedStatus(
                        result.Usage,
                        settings.Storage.MaxUsageBytes,
                        result.DeletedCount,
                        result.Errors.FirstOrDefault()
                    )
                );
                return;
            }

            var usage = await _getUsage(settings, cancellationToken);
            SetStatus(CreateCompletedStatus(usage, settings.Storage.MaxUsageBytes, 0, null));
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

    private bool IsCurrentOperation(
        int refreshOrRetentionVersion,
        int usageRefreshVersion,
        bool allowRetention
    )
    {
        return refreshOrRetentionVersion == Volatile.Read(ref _refreshOrRetentionVersion)
            && (allowRetention || usageRefreshVersion == Volatile.Read(ref _usageRefreshVersion));
    }

    private static RecordingStorageStatus CreateCompletedStatus(
        RecordingStorageUsage usage,
        long maxUsageBytes,
        int deletedRecordingCount,
        Exception? error
    )
    {
        return new RecordingStorageStatus(
            usage.UsageBytes,
            maxUsageBytes,
            usage.RecordingCount,
            IsRefreshing: false,
            IsCleaning: false,
            LastDeletedRecordingCount: deletedRecordingCount,
            LastError: error,
            FavoriteUsageBytes: usage.FavoriteUsageBytes,
            FavoriteRecordingCount: usage.FavoriteRecordingCount
        );
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
