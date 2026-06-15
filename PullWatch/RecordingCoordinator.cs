using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

    private readonly IRecordingService _recordingService;
    private readonly ILogger<RecordingCoordinator> _logger;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _notificationLock = new();
    private Task _notificationQueue = Task.CompletedTask;
    private RecordingCoordinatorStatus _status = new(
        RecordingCoordinatorState.Idle,
        null,
        null,
        null,
        null,
        null);
    private bool _disposed;

    private enum StopRequestKind
    {
        Automatic,
        Manual,
        Shutdown
    }

    public RecordingCoordinator(
        IRecordingService recordingService,
        ILogger<RecordingCoordinator> logger,
        TimeSpan? startTimeout = null,
        TimeSpan? stopTimeout = null)
    {
        _recordingService = recordingService;
        _logger = logger;
        _startTimeout = startTimeout ?? DefaultStartTimeout;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        _recordingService.Failed += OnRecordingServiceFailed;
    }

    public event Action<RecordingCoordinatorStatus>? StatusChanged;

    public RecordingCoordinatorStatus Status => Volatile.Read(ref _status);

    public Task<RecordingCommandResult> StartAutomaticAsync(
        RecordingOwner owner,
        string? identity,
        CancellationToken cancellationToken)
    {
        if (owner == RecordingOwner.Manual)
        {
            throw new ArgumentException("Automatic recording owner cannot be manual.", nameof(owner));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return StartCoreAsync(owner, identity).WaitAsync(cancellationToken);
    }

    public Task<RecordingCommandResult> StartManualAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartCoreAsync(RecordingOwner.Manual, null).WaitAsync(cancellationToken);
    }

    public Task<RecordingCommandResult> StopAutomaticAsync(
        RecordingOwner owner,
        string? identity,
        CancellationToken cancellationToken)
    {
        if (owner == RecordingOwner.Manual)
        {
            throw new ArgumentException("Automatic recording owner cannot be manual.", nameof(owner));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return StopCoreAsync(owner, identity, StopRequestKind.Automatic)
            .WaitAsync(cancellationToken);
    }

    public Task<RecordingCommandResult> StopManualAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StopCoreAsync(null, null, StopRequestKind.Manual)
            .WaitAsync(cancellationToken);
    }

    public Task<RecordingCommandResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StopCoreAsync(null, null, StopRequestKind.Shutdown)
            .WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recordingService.Failed -= OnRecordingServiceFailed;

        try
        {
            await ShutdownAsync(CancellationToken.None);
        }
        finally
        {
            await _recordingService.DisposeAsync();
        }
    }

    private async Task<RecordingCommandResult> StartCoreAsync(RecordingOwner owner, string? identity)
    {
        await _operationLock.WaitAsync(CancellationToken.None);

        try
        {
            var status = Status;

            if (status.State != RecordingCoordinatorState.Idle)
            {
                _logger.LogInformation(
                    "Ignoring {RecordingOwner} recording start because {ActiveOwner} owns state {RecordingState}",
                    owner,
                    status.Owner,
                    status.State);
                return RecordingCommandResult.AlreadyActive;
            }

            if (owner != RecordingOwner.Manual && status.SuppressedUntilOwnerEnd is not null)
            {
                _logger.LogInformation(
                    "Ignoring {RecordingOwner} recording start until the manually stopped {SuppressedOwner} activity ends",
                    owner,
                    status.SuppressedUntilOwnerEnd);
                return RecordingCommandResult.Suppressed;
            }

            PublishStatus(RecordingCoordinatorState.Starting, owner, identity);
            var startTask = _recordingService.StartAsync(CancellationToken.None);

            try
            {
                await startTask.WaitAsync(_startTimeout);
                PublishStatus(RecordingCoordinatorState.Recording, owner, identity);
                return RecordingCommandResult.Started;
            }
            catch (TimeoutException exception)
            {
                var failure = new TimeoutException(
                    $"Recording start timed out after {_startTimeout}.",
                    exception);
                PublishStatus(
                    RecordingCoordinatorState.Stopping,
                    owner,
                    identity,
                    failure);
                _ = ObserveBackgroundTaskAsync(startTask, "Recorder startup failed after timing out");
                _ = ObserveCleanupAsync(_recordingService.StopAsync(CancellationToken.None));
                _logger.LogError(exception, "Recording start timed out for {RecordingOwner}", owner);
                return RecordingCommandResult.TimedOut;
            }
            catch (Exception exception)
            {
                PublishStatus(RecordingCoordinatorState.Idle, null, null, exception);
                _logger.LogError(exception, "Could not start {RecordingOwner} recording", owner);
                return RecordingCommandResult.Failed;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<RecordingCommandResult> StopCoreAsync(
        RecordingOwner? requestedOwner,
        string? requestedIdentity,
        StopRequestKind requestKind)
    {
        await _operationLock.WaitAsync(CancellationToken.None);

        try
        {
            var status = Status;
            var suppressionEndMatched =
                requestKind == StopRequestKind.Automatic &&
                IsSuppressionEndMatch(requestedOwner, requestedIdentity);

            if (suppressionEndMatched)
            {
                ClearSuppression();
                status = Status;
            }

            if (status.State == RecordingCoordinatorState.Idle)
            {
                return RecordingCommandResult.NoActiveRecording;
            }

            if (requestKind == StopRequestKind.Automatic &&
                !IsActivityMatch(status, requestedOwner, requestedIdentity))
            {
                _logger.LogInformation(
                    "Ignoring recording stop for {RequestedOwner} because recording is owned by {ActiveOwner}",
                    requestedOwner,
                    status.Owner);
                return RecordingCommandResult.OwnerMismatch;
            }

            if (requestKind == StopRequestKind.Manual &&
                status.Owner is not null and not RecordingOwner.Manual)
            {
                SetSuppression(status.Owner.Value, status.Identity);
            }

            PublishStatus(RecordingCoordinatorState.Stopping, status.Owner, status.Identity);
            var stopTask = _recordingService.StopAsync(CancellationToken.None);

            try
            {
                await stopTask.WaitAsync(_stopTimeout);
                PublishStatus(RecordingCoordinatorState.Idle, null, null);
                return RecordingCommandResult.Stopped;
            }
            catch (TimeoutException exception)
            {
                var failure = new TimeoutException(
                    $"Recording stop timed out after {_stopTimeout}.",
                    exception);
                PublishStatus(
                    RecordingCoordinatorState.Stopping,
                    status.Owner,
                    status.Identity,
                    failure);
                _ = ObserveCleanupAsync(stopTask);
                _logger.LogError(exception, "Recording stop timed out for {RecordingOwner}", status.Owner);
                return RecordingCommandResult.TimedOut;
            }
            catch (Exception exception)
            {
                PublishStatus(RecordingCoordinatorState.Idle, null, null, exception);
                _logger.LogError(exception, "Could not stop {RecordingOwner} recording", status.Owner);
                return RecordingCommandResult.Failed;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task ObserveBackgroundTaskAsync(Task task, string errorMessage)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, errorMessage);
        }
    }

    private async Task ObserveCleanupAsync(Task cleanupTask)
    {
        try
        {
            await cleanupTask;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Recorder cleanup failed after an operation timeout");
            return;
        }

        await _operationLock.WaitAsync(CancellationToken.None);

        try
        {
            if (Status.State == RecordingCoordinatorState.Stopping)
            {
                PublishStatus(RecordingCoordinatorState.Idle, null, null);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void OnRecordingServiceFailed(object? sender, RecordingServiceFailedEventArgs eventArgs)
    {
        _ = RecoverFromRecorderFailureAsync(eventArgs.Exception);
    }

    private async Task RecoverFromRecorderFailureAsync(Exception exception)
    {
        await _operationLock.WaitAsync(CancellationToken.None);

        try
        {
            if (Status.State != RecordingCoordinatorState.Idle)
            {
                PublishStatus(RecordingCoordinatorState.Idle, null, null, exception);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private bool IsSuppressionEndMatch(RecordingOwner? owner, string? identity)
    {
        var status = Status;
        return status.SuppressedUntilOwnerEnd == owner &&
               IdentitiesMatch(status.SuppressedIdentity, identity);
    }

    private static bool IsActivityMatch(
        RecordingCoordinatorStatus status,
        RecordingOwner? owner,
        string? identity)
    {
        return status.Owner == owner && IdentitiesMatch(status.Identity, identity);
    }

    private static bool IdentitiesMatch(string? activeIdentity, string? requestedIdentity)
    {
        return activeIdentity is null ||
               requestedIdentity is null ||
               StringComparer.Ordinal.Equals(activeIdentity, requestedIdentity);
    }

    private void SetSuppression(RecordingOwner owner, string? identity)
    {
        SetStatus(Status with
        {
            SuppressedUntilOwnerEnd = owner,
            SuppressedIdentity = identity
        });
    }

    private void ClearSuppression()
    {
        SetStatus(Status with
        {
            SuppressedUntilOwnerEnd = null,
            SuppressedIdentity = null
        });
    }

    private void PublishStatus(
        RecordingCoordinatorState state,
        RecordingOwner? owner,
        string? identity,
        Exception? lastFailure = null,
        RecordingOwner? suppressedUntilOwnerEnd = null)
    {
        var current = Status;
        var snapshot = new RecordingCoordinatorStatus(
            state,
            owner,
            identity,
            suppressedUntilOwnerEnd ?? current.SuppressedUntilOwnerEnd,
            current.SuppressedIdentity,
            lastFailure ?? current.LastFailure);

        SetStatus(snapshot);
    }

    private void SetStatus(RecordingCoordinatorStatus snapshot)
    {
        Volatile.Write(ref _status, snapshot);
        NotifyStatusChanged(snapshot);
    }

    private void NotifyStatusChanged(RecordingCoordinatorStatus snapshot)
    {
        lock (_notificationLock)
        {
            _notificationQueue = _notificationQueue.ContinueWith(
                _ => PublishStatusNotification(snapshot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void PublishStatusNotification(RecordingCoordinatorStatus snapshot)
    {
        var handlers = StatusChanged;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<RecordingCoordinatorStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Recording coordinator status subscriber failed");
            }
        }
    }
}
