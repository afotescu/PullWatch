using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly IRecordingService _recordingService;
    private readonly RecordingCatalog? _recordingCatalog;
    private readonly ILogger<RecordingCoordinator> _logger;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeSpan _disposeTimeout;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _notificationLock = new();
    private Task _notificationQueue = Task.CompletedTask;
    private int _expectedCount;
    private int _savedCount;
    private RecordingCoordinatorStatus _status = new(
        RecordingCoordinatorState.Idle,
        null,
        null,
        null,
        null,
        null,
        null,
        null
    );
    private Guid? _activeCatalogRecordingId;
    private bool _disposed;

    private enum StopRequestKind
    {
        Automatic,
        Manual,
        Shutdown,
    }

    public RecordingCoordinator(
        IRecordingService recordingService,
        ILogger<RecordingCoordinator> logger,
        TimeSpan? startTimeout = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? disposeTimeout = null,
        RecordingCatalog? recordingCatalog = null
    )
    {
        _recordingService = recordingService;
        _recordingCatalog = recordingCatalog;
        _logger = logger;
        _startTimeout = startTimeout ?? DefaultStartTimeout;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        _disposeTimeout = disposeTimeout ?? DefaultDisposeTimeout;
        _recordingService.Failed += OnRecordingServiceFailed;
        _recordingService.CaptureTargetExited += OnCaptureTargetExited;
    }

    public event Action<RecordingCoordinatorStatus>? StatusChanged;

    public RecordingCoordinatorStatus Status => Volatile.Read(ref _status);

    public Task<RecordingCommandResult> StartAutomaticAsync(
        RecordingContext context,
        CancellationToken cancellationToken
    )
    {
        if (context is ManualRecordingContext)
        {
            throw new ArgumentException(
                "Automatic recording context cannot be manual.",
                nameof(context)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        return StartCoreAsync(context, cancellationToken);
    }

    public Task<RecordingCommandResult> StartManualAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartCoreAsync(new ManualRecordingContext(DateTimeOffset.Now), cancellationToken);
    }

    public Task<RecordingCommandResult> StopAutomaticAsync(
        RecordingOwner owner,
        string? identity,
        CancellationToken cancellationToken
    )
    {
        return StopAutomaticAsync(owner, identity, null, cancellationToken);
    }

    public Task<RecordingCommandResult> StopAutomaticAsync(
        RecordingOwner owner,
        string? identity,
        RecordingActivityEnd? activityEnd,
        CancellationToken cancellationToken
    )
    {
        if (owner == RecordingOwner.Manual)
        {
            throw new ArgumentException(
                "Automatic recording owner cannot be manual.",
                nameof(owner)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        return StopCoreAsync(
            owner,
            identity,
            activityEnd,
            StopRequestKind.Automatic,
            cancellationToken
        );
    }

    public Task<RecordingCommandResult> StopManualAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StopCoreAsync(null, null, null, StopRequestKind.Manual, cancellationToken);
    }

    public Task<RecordingCommandResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StopCoreAsync(null, null, null, StopRequestKind.Shutdown, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recordingService.Failed -= OnRecordingServiceFailed;
        _recordingService.CaptureTargetExited -= OnCaptureTargetExited;

        try
        {
            await ShutdownAsync(CancellationToken.None);
        }
        finally
        {
            Task? disposeTask = null;

            try
            {
                disposeTask = Task.Run(
                    async () => await _recordingService.DisposeAsync(),
                    CancellationToken.None
                );
                await disposeTask.WaitAsync(_disposeTimeout);
            }
            catch (TimeoutException exception)
            {
                if (disposeTask is not null)
                {
                    _ = ObserveBackgroundTaskAsync(
                        disposeTask,
                        "Recording service disposal failed after timing out"
                    );
                }

                _logger.LogError(
                    exception,
                    "Recording service disposal timed out after {DisposeTimeout}",
                    _disposeTimeout
                );
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Recording service disposal failed");
            }
        }
    }

    private async Task<RecordingCommandResult> StartCoreAsync(
        RecordingContext context,
        CancellationToken cancellationToken
    )
    {
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = GetOwner(context);
            var identity = GetIdentity(context);
            var status = Status;

            if (status.State != RecordingCoordinatorState.Idle)
            {
                _logger.LogInformation(
                    "Ignoring {RecordingOwner} recording start because {ActiveOwner} owns state {RecordingState}",
                    owner,
                    status.Owner,
                    status.State
                );
                return RecordingCommandResult.AlreadyActive;
            }

            if (owner != RecordingOwner.Manual && status.SuppressedUntilOwnerEnd is not null)
            {
                _logger.LogInformation(
                    "Ignoring {RecordingOwner} recording start until the manually stopped {SuppressedOwner} activity ends",
                    owner,
                    status.SuppressedUntilOwnerEnd
                );
                return RecordingCommandResult.Suppressed;
            }

            Task? startTask = null;
            Guid? catalogRecordingId = null;
            var shouldStopAfterCatalogStartupFailure = false;
            _expectedCount++;

            try
            {
                startTask = _recordingService.StartAsync(context, CancellationToken.None);
                shouldStopAfterCatalogStartupFailure = _recordingCatalog is not null;
                catalogRecordingId = await BeginCatalogRecordingAsync(context, startTask);
                shouldStopAfterCatalogStartupFailure = false;
                PublishStatus(
                    RecordingCoordinatorState.Starting,
                    owner,
                    identity,
                    context,
                    activeOutputPath: _recordingService.ActiveOutputPath,
                    clearLastFailure: true
                );
                await startTask.WaitAsync(_startTimeout);
                PublishStatus(
                    RecordingCoordinatorState.Recording,
                    owner,
                    identity,
                    context,
                    activeOutputPath: _recordingService.ActiveOutputPath
                );
                return RecordingCommandResult.Started;
            }
            catch (Exception exception)
                when (RecordingFailureClassifier.IsTargetUnavailable(exception))
            {
                _expectedCount--;
                await RemoveCatalogRecordingAsync(catalogRecordingId);
                PublishStatus(
                    RecordingCoordinatorState.Idle,
                    null,
                    null,
                    null,
                    clearLastFailure: true
                );
                _logger.LogInformation(
                    exception,
                    "Could not start {RecordingOwner} recording because the capture target is unavailable",
                    owner
                );
                return RecordingCommandResult.TargetUnavailable;
            }
            catch (TimeoutException exception)
            {
                await RemoveCatalogRecordingAsync(catalogRecordingId);
                var failure = new TimeoutException(
                    $"Recording start timed out after {_startTimeout}.",
                    exception
                );
                PublishStatus(
                    RecordingCoordinatorState.Stopping,
                    owner,
                    identity,
                    context,
                    failure
                );
                if (startTask is not null)
                {
                    _ = ObserveBackgroundTaskAsync(
                        startTask,
                        "Recorder startup failed after timing out"
                    );
                }

                _ = ObserveCleanupAsync(
                    _recordingService.StopAsync(CancellationToken.None),
                    null,
                    null,
                    countAsSaved: false
                );
                _logger.LogError(
                    exception,
                    "Recording start timed out for {RecordingOwner}",
                    owner
                );
                return RecordingCommandResult.TimedOut;
            }
            catch (Exception exception)
            {
                await RemoveCatalogRecordingAsync(catalogRecordingId);
                PublishStatus(RecordingCoordinatorState.Idle, null, null, null, exception);
                if (startTask is not null && shouldStopAfterCatalogStartupFailure)
                {
                    _ = ObserveCleanupAsync(
                        _recordingService.StopAsync(CancellationToken.None),
                        null,
                        null,
                        countAsSaved: false
                    );
                }

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
        RecordingActivityEnd? activityEnd,
        StopRequestKind requestKind,
        CancellationToken cancellationToken
    )
    {
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = Status;
            var suppressionEndMatched =
                requestKind == StopRequestKind.Automatic
                && IsSuppressionEndMatch(requestedOwner, requestedIdentity);

            if (suppressionEndMatched)
            {
                ClearSuppression();
                status = Status;
            }

            if (status.State == RecordingCoordinatorState.Idle)
            {
                return RecordingCommandResult.NoActiveRecording;
            }

            if (
                requestKind == StopRequestKind.Automatic
                && !IsActivityMatch(status, requestedOwner, requestedIdentity)
            )
            {
                _logger.LogInformation(
                    "Ignoring recording stop for {RequestedOwner} because recording is owned by {ActiveOwner}",
                    requestedOwner,
                    status.Owner
                );
                return RecordingCommandResult.OwnerMismatch;
            }

            if (
                requestKind == StopRequestKind.Manual
                && status.Owner is not null and not RecordingOwner.Manual
            )
            {
                SetSuppression(status.Owner.Value, status.Identity);
            }

            PublishStatus(
                RecordingCoordinatorState.Stopping,
                status.Owner,
                status.Identity,
                status.Context
            );
            var catalogRecordingId = _activeCatalogRecordingId;
            var stopTask = _recordingService.StopAsync(CancellationToken.None);

            try
            {
                await stopTask.WaitAsync(_stopTimeout);
                await CompleteCatalogRecordingAsync(catalogRecordingId, activityEnd);
                _savedCount++;
                PublishStatus(RecordingCoordinatorState.Idle, null, null, null);
                return RecordingCommandResult.Stopped;
            }
            catch (TimeoutException exception)
            {
                var failure = new TimeoutException(
                    $"Recording stop timed out after {_stopTimeout}.",
                    exception
                );
                PublishStatus(
                    RecordingCoordinatorState.Stopping,
                    status.Owner,
                    status.Identity,
                    status.Context,
                    failure
                );
                _ = ObserveCleanupAsync(
                    stopTask,
                    catalogRecordingId,
                    activityEnd,
                    countAsSaved: true
                );
                _logger.LogError(
                    exception,
                    "Recording stop timed out for {RecordingOwner}",
                    status.Owner
                );
                return RecordingCommandResult.TimedOut;
            }
            catch (Exception exception)
            {
                await RemoveCatalogRecordingAsync(catalogRecordingId);
                PublishStatus(RecordingCoordinatorState.Idle, null, null, null, exception);
                _logger.LogError(
                    exception,
                    "Could not stop {RecordingOwner} recording",
                    status.Owner
                );
                return RecordingCommandResult.Failed;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<Guid?> BeginCatalogRecordingAsync(RecordingContext context, Task startTask)
    {
        if (_recordingCatalog is null)
        {
            return null;
        }

        var activeOutputPath = _recordingService.ActiveOutputPath;

        if (string.IsNullOrWhiteSpace(activeOutputPath) && startTask.IsCompleted)
        {
            await startTask;
            activeOutputPath = _recordingService.ActiveOutputPath;
        }

        if (string.IsNullOrWhiteSpace(activeOutputPath))
        {
            throw new InvalidOperationException("Recorder did not report an output path.");
        }

        var recordingId = await _recordingCatalog.BeginRecordingAsync(
            context,
            activeOutputPath,
            CancellationToken.None
        );
        _activeCatalogRecordingId = recordingId;
        return recordingId;
    }

    private async Task CompleteCatalogRecordingAsync(
        Guid? recordingId,
        RecordingActivityEnd? activityEnd
    )
    {
        if (_recordingCatalog is null || recordingId is null)
        {
            return;
        }

        await _recordingCatalog.CompleteRecordingAsync(
            recordingId.Value,
            DateTimeOffset.UtcNow,
            activityEnd,
            CancellationToken.None
        );
        ClearActiveCatalogRecording(recordingId);
    }

    private async Task RemoveCatalogRecordingAsync(Guid? recordingId)
    {
        if (_recordingCatalog is null || recordingId is null)
        {
            return;
        }

        try
        {
            await _recordingCatalog.RemoveRecordingAsync(recordingId.Value, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not remove catalog row for failed recording {RecordingId}",
                recordingId
            );
        }

        ClearActiveCatalogRecording(recordingId);
    }

    private void ClearActiveCatalogRecording(Guid? recordingId)
    {
        if (_activeCatalogRecordingId == recordingId)
        {
            _activeCatalogRecordingId = null;
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

    private async Task ObserveCleanupAsync(
        Task cleanupTask,
        Guid? catalogRecordingId,
        RecordingActivityEnd? activityEnd,
        bool countAsSaved
    )
    {
        try
        {
            await cleanupTask;
        }
        catch (Exception exception)
        {
            await RemoveCatalogRecordingAsync(catalogRecordingId);
            _logger.LogError(exception, "Recorder cleanup failed after an operation timeout");
            return;
        }

        await _operationLock.WaitAsync(CancellationToken.None);

        try
        {
            if (Status.State == RecordingCoordinatorState.Stopping)
            {
                try
                {
                    if (countAsSaved)
                    {
                        await CompleteCatalogRecordingAsync(catalogRecordingId, activityEnd);
                        _savedCount++;
                    }

                    PublishStatus(RecordingCoordinatorState.Idle, null, null, null);
                }
                catch (Exception exception)
                {
                    await RemoveCatalogRecordingAsync(catalogRecordingId);
                    PublishStatus(RecordingCoordinatorState.Idle, null, null, null, exception);
                    _logger.LogError(
                        exception,
                        "Recording cleanup completed, but catalog finalization failed"
                    );
                }
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

    private void OnCaptureTargetExited(object? sender, EventArgs eventArgs)
    {
        _logger.LogInformation("Stopping recording because the capture target exited");
        _ = StopAfterCaptureTargetExitAsync();
    }

    private async Task StopAfterCaptureTargetExitAsync()
    {
        var result = await StopCoreAsync(
            null,
            null,
            null,
            StopRequestKind.Shutdown,
            CancellationToken.None
        );

        if (result is RecordingCommandResult.Failed or RecordingCommandResult.TimedOut)
        {
            _logger.LogError(
                "Could not gracefully stop recording after the capture target exited: {RecordingCommandResult}",
                result
            );
        }
    }

    private async Task RecoverFromRecorderFailureAsync(Exception exception)
    {
        await _operationLock.WaitAsync(CancellationToken.None);

        try
        {
            if (Status.State != RecordingCoordinatorState.Idle)
            {
                await RemoveCatalogRecordingAsync(_activeCatalogRecordingId);
                PublishStatus(RecordingCoordinatorState.Idle, null, null, null, exception);
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
        return status.SuppressedUntilOwnerEnd == owner
            && IdentitiesMatch(status.SuppressedIdentity, identity);
    }

    private static bool IsActivityMatch(
        RecordingCoordinatorStatus status,
        RecordingOwner? owner,
        string? identity
    )
    {
        return status.Owner == owner && IdentitiesMatch(status.Identity, identity);
    }

    private static bool IdentitiesMatch(string? activeIdentity, string? requestedIdentity)
    {
        return activeIdentity is null
            || requestedIdentity is null
            || StringComparer.Ordinal.Equals(activeIdentity, requestedIdentity);
    }

    private static RecordingOwner GetOwner(RecordingContext context)
    {
        return context switch
        {
            ManualRecordingContext => RecordingOwner.Manual,
            ChallengeRecordingContext => RecordingOwner.ChallengeMode,
            EncounterRecordingContext => RecordingOwner.Encounter,
            _ => throw new ArgumentOutOfRangeException(
                nameof(context),
                context,
                "Unknown recording context."
            ),
        };
    }

    private static string? GetIdentity(RecordingContext context)
    {
        return context is EncounterRecordingContext encounter
            ? encounter.EncounterId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private void SetSuppression(RecordingOwner owner, string? identity)
    {
        SetStatus(Status with { SuppressedUntilOwnerEnd = owner, SuppressedIdentity = identity });
    }

    private void ClearSuppression()
    {
        SetStatus(Status with { SuppressedUntilOwnerEnd = null, SuppressedIdentity = null });
    }

    private void PublishStatus(
        RecordingCoordinatorState state,
        RecordingOwner? owner,
        string? identity,
        RecordingContext? context,
        Exception? lastFailure = null,
        RecordingOwner? suppressedUntilOwnerEnd = null,
        string? activeOutputPath = null,
        bool clearLastFailure = false
    )
    {
        var current = Status;
        var snapshot = new RecordingCoordinatorStatus(
            state,
            owner,
            identity,
            context,
            suppressedUntilOwnerEnd ?? current.SuppressedUntilOwnerEnd,
            current.SuppressedIdentity,
            clearLastFailure ? null : lastFailure ?? current.LastFailure,
            state == RecordingCoordinatorState.Idle
                ? null
                : activeOutputPath ?? current.ActiveOutputPath
        )
        {
            Statistics = new RecordingStatistics(_expectedCount, _savedCount),
        };

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
                TaskScheduler.Default
            );
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
