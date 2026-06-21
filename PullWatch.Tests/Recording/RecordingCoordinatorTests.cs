using Microsoft.Extensions.Logging.Abstractions;
using PullWatch.Tests.TestDoubles;

namespace PullWatch.Tests;

public sealed class RecordingCoordinatorTests
{
    [Fact]
    public async Task MatchingOwnerAndIdentityStopsRecording()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);
        var context = Encounter(123);

        var startResult = await coordinator.StartAutomaticAsync(context, CancellationToken.None);
        var wrongEndResult = await coordinator.StopAutomaticAsync(
            RecordingOwner.Encounter,
            "456",
            CancellationToken.None
        );
        var matchingEndResult = await coordinator.StopAutomaticAsync(
            RecordingOwner.Encounter,
            "123",
            CancellationToken.None
        );

        Assert.Equal(RecordingCommandResult.Started, startResult);
        Assert.Equal(RecordingCommandResult.OwnerMismatch, wrongEndResult);
        Assert.Equal(RecordingCommandResult.Stopped, matchingEndResult);
        Assert.Equal(["start", "stop"], recorder.Calls);
        Assert.Same(context, recorder.StartedContexts.Single());
    }

    [Fact]
    public async Task ManualStopSuppressesAutomaticStartsUntilMatchingOwnerEnds()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartAutomaticAsync(Challenge(), CancellationToken.None);
        await coordinator.StopManualAsync(CancellationToken.None);

        var nestedEncounterResult = await coordinator.StartAutomaticAsync(
            Encounter(123),
            CancellationToken.None
        );
        await coordinator.StopAutomaticAsync(
            RecordingOwner.ChallengeMode,
            null,
            CancellationToken.None
        );
        var nextEncounterResult = await coordinator.StartAutomaticAsync(
            Encounter(456),
            CancellationToken.None
        );

        Assert.Equal(RecordingCommandResult.Suppressed, nestedEncounterResult);
        Assert.Equal(RecordingCommandResult.Started, nextEncounterResult);
        Assert.Equal(["start", "stop", "start"], recorder.Calls);
    }

    [Fact]
    public async Task ManualStartIsRejectedWhileAutomaticRecordingIsActive()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartAutomaticAsync(Challenge(), CancellationToken.None);
        var result = await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(RecordingCommandResult.AlreadyActive, result);
        Assert.Equal(["start"], recorder.Calls);
    }

    [Fact]
    public async Task SuppressionEndDoesNotStopActiveManualRecording()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartAutomaticAsync(Challenge(), CancellationToken.None);
        await coordinator.StopManualAsync(CancellationToken.None);
        await coordinator.StartManualAsync(CancellationToken.None);

        var endResult = await coordinator.StopAutomaticAsync(
            RecordingOwner.ChallengeMode,
            null,
            CancellationToken.None
        );

        Assert.Equal(RecordingCommandResult.OwnerMismatch, endResult);
        Assert.Null(coordinator.Status.SuppressedUntilOwnerEnd);
        Assert.Equal(RecordingOwner.Manual, coordinator.Status.Owner);
        Assert.Equal(["start", "stop", "start"], recorder.Calls);
    }

    [Fact]
    public async Task StopWaitsForPendingStartBecauseOperationsAreSerialized()
    {
        var pendingStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var recorder = new FakeRecordingService { PendingStart = pendingStart };
        await using var coordinator = CreateCoordinator(recorder);

        var startTask = coordinator.StartAutomaticAsync(Encounter(123), CancellationToken.None);
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Starting);

        var stopTask = coordinator.StopAutomaticAsync(
            RecordingOwner.Encounter,
            "123",
            CancellationToken.None
        );

        Assert.False(stopTask.IsCompleted);
        pendingStart.SetResult();

        Assert.Equal(RecordingCommandResult.Started, await startTask);
        Assert.Equal(RecordingCommandResult.Stopped, await stopTask);
        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelAcceptedStart()
    {
        var pendingStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var recorder = new FakeRecordingService { PendingStart = pendingStart };
        await using var coordinator = CreateCoordinator(recorder);
        using var cancellation = new CancellationTokenSource();

        var startTask = coordinator.StartManualAsync(cancellation.Token);
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Starting);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        pendingStart.SetResult();
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Recording);

        Assert.Equal(RecordingOwner.Manual, coordinator.Status.Owner);
    }

    [Fact]
    public async Task RecorderFailureReturnsToIdleAndRetainsFailure()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);
        recorder.RaiseFailure(new InvalidOperationException("capture failed"));
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Idle);

        Assert.Equal("capture failed", coordinator.Status.LastFailure?.Message);
    }

    [Fact]
    public async Task CaptureTargetExitGracefullyStopsRecording()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);
        recorder.RaiseCaptureTargetExited();
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Idle);

        Assert.Equal(["start", "stop"], recorder.Calls);
        Assert.Null(coordinator.Status.LastFailure);
    }

    [Fact]
    public async Task CaptureTargetExitDuringStartupStopsAfterStartupCompletes()
    {
        var pendingStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var recorder = new FakeRecordingService { PendingStart = pendingStart };
        await using var coordinator = CreateCoordinator(recorder);

        var startTask = coordinator.StartManualAsync(CancellationToken.None);
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Starting);
        recorder.RaiseCaptureTargetExited();
        pendingStart.SetResult();

        Assert.Equal(RecordingCommandResult.Started, await startTask);
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Idle);
        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task StopTimeoutBlocksStartsUntilCleanupCompletes()
    {
        var pendingStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var recorder = new FakeRecordingService { PendingStop = pendingStop };
        await using var coordinator = CreateCoordinator(
            recorder,
            stopTimeout: TimeSpan.FromMilliseconds(20)
        );

        await coordinator.StartManualAsync(CancellationToken.None);
        var stopResult = await coordinator.StopManualAsync(CancellationToken.None);
        var blockedStartResult = await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(RecordingCommandResult.TimedOut, stopResult);
        Assert.Equal(RecordingCommandResult.AlreadyActive, blockedStartResult);
        Assert.Equal(RecordingCoordinatorState.Stopping, coordinator.Status.State);

        pendingStop.SetResult();
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Idle);

        Assert.Equal(new RecordingStatistics(1, 1), coordinator.Status.Statistics);
        Assert.Equal(
            RecordingCommandResult.Started,
            await coordinator.StartManualAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task StartConfirmationTimeoutStopsAndReleasesRecorder()
    {
        var pendingStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var recorder = new FakeRecordingService { PendingStart = pendingStart };
        await using var coordinator = CreateCoordinator(
            recorder,
            startTimeout: TimeSpan.FromMilliseconds(20)
        );

        var result = await coordinator.StartManualAsync(CancellationToken.None);
        await WaitForStateAsync(coordinator, RecordingCoordinatorState.Idle);
        pendingStart.TrySetException(new InvalidOperationException("stopped before startup"));

        Assert.Equal(RecordingCommandResult.TimedOut, result);
        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task StopFailureReleasesOwnershipAndAllowsRetry()
    {
        var recorder = new FakeRecordingService
        {
            StopException = new InvalidOperationException("finalization failed"),
        };
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);
        var stopResult = await coordinator.StopManualAsync(CancellationToken.None);
        Assert.Equal(new RecordingStatistics(1, 0), coordinator.Status.Statistics);

        recorder.StopException = null;
        var retryResult = await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(RecordingCommandResult.Failed, stopResult);
        Assert.Equal("finalization failed", coordinator.Status.LastFailure?.Message);
        Assert.Equal(RecordingCommandResult.Started, retryResult);
    }

    [Fact]
    public async Task ShutdownStopsActiveRecordingExactlyOnce()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);
        var firstResult = await coordinator.ShutdownAsync(CancellationToken.None);
        var secondResult = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(RecordingCommandResult.Stopped, firstResult);
        Assert.Equal(RecordingCommandResult.NoActiveRecording, secondResult);
        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task DisposeReturnsWhenRecordingServiceDisposalTimesOut()
    {
        var pendingDispose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var recorder = new FakeRecordingService { PendingDispose = pendingDispose };
        var coordinator = CreateCoordinator(
            recorder,
            disposeTimeout: TimeSpan.FromMilliseconds(20)
        );

        await coordinator.DisposeAsync();

        Assert.Equal(1, recorder.DisposeCalls);
        Assert.False(pendingDispose.Task.IsCompleted);
        pendingDispose.SetResult();
    }

    [Fact]
    public async Task DisposeReturnsWhenRecordingServiceDisposalBlocksSynchronously()
    {
        using var disposalStarted = new ManualResetEventSlim();
        using var disposeBlocker = new ManualResetEventSlim();
        var recorder = new FakeRecordingService
        {
            DisposeAction = () =>
            {
                disposalStarted.Set();
                disposeBlocker.Wait();
            },
        };
        var coordinator = CreateCoordinator(
            recorder,
            disposeTimeout: TimeSpan.FromMilliseconds(20)
        );

        await coordinator.DisposeAsync();

        try
        {
            Assert.True(
                disposalStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)
            );
            Assert.Equal(1, recorder.DisposeCalls);
        }
        finally
        {
            disposeBlocker.Set();
        }
    }

    [Fact]
    public async Task ActiveOutputPathIsExposedAndClearedWhenIdle()
    {
        var recorder = new FakeRecordingService { ActiveOutputPath = @"C:\Recordings\manual.mp4" };
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(recorder.ActiveOutputPath, coordinator.Status.ActiveOutputPath);

        await coordinator.StopManualAsync(CancellationToken.None);

        Assert.Null(coordinator.Status.ActiveOutputPath);
    }

    [Fact]
    public async Task CatalogRowTracksSuccessfulRecordingLifecycle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var outputPath = Path.Combine(database.DirectoryPath, "manual.mp4");
        var recorder = new FakeRecordingService { ActiveOutputPath = outputPath };
        await using var coordinator = CreateCoordinator(
            recorder,
            recordingCatalog: database.Catalog
        );

        Assert.Equal(
            RecordingCommandResult.Started,
            await coordinator.StartManualAsync(cancellationToken)
        );
        var started = Assert.Single(await database.Repository.ListAsync(cancellationToken));

        File.WriteAllText(outputPath, "recording");
        Assert.Equal(
            RecordingCommandResult.Stopped,
            await coordinator.StopManualAsync(cancellationToken)
        );
        var completed = await database.Repository.GetByIdAsync(started.Id, cancellationToken);

        Assert.Equal(RecordingCatalogStatus.Recording, started.Status);
        Assert.Equal(RecordingCatalogKind.Manual, started.Kind);
        Assert.NotNull(completed);
        Assert.Equal(RecordingCatalogStatus.Available, completed.Status);
        Assert.Equal(9, completed.FileSizeBytes);
        Assert.NotNull(completed.EndedAtUtc);
    }

    [Fact]
    public async Task CatalogRowIsRemovedWhenStartupFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var recorder = new FakeRecordingService
        {
            ActiveOutputPath = Path.Combine(database.DirectoryPath, "failed.mp4"),
            StartException = new InvalidOperationException("capture failed"),
        };
        await using var coordinator = CreateCoordinator(
            recorder,
            recordingCatalog: database.Catalog
        );

        Assert.Equal(
            RecordingCommandResult.Failed,
            await coordinator.StartManualAsync(cancellationToken)
        );

        Assert.Empty(await database.Repository.ListAsync(cancellationToken));
    }

    [Fact]
    public async Task CatalogRowIsRemovedWhenFinalizationFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var outputPath = Path.Combine(database.DirectoryPath, "failed.mp4");
        var recorder = new FakeRecordingService
        {
            ActiveOutputPath = outputPath,
            StopException = new InvalidOperationException("finalization failed"),
        };
        await using var coordinator = CreateCoordinator(
            recorder,
            recordingCatalog: database.Catalog
        );

        await coordinator.StartManualAsync(cancellationToken);
        var started = Assert.Single(await database.Repository.ListAsync(cancellationToken));

        Assert.Equal(
            RecordingCommandResult.Failed,
            await coordinator.StopManualAsync(cancellationToken)
        );

        Assert.Null(await database.Repository.GetByIdAsync(started.Id, cancellationToken));
    }

    [Fact]
    public async Task StatisticsCountAcceptedRequestsAndSuccessfulFinalizations()
    {
        var recorder = new FakeRecordingService();
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(new RecordingStatistics(1, 0), coordinator.Status.Statistics);

        await coordinator.StartManualAsync(CancellationToken.None);
        await coordinator.StopManualAsync(CancellationToken.None);

        Assert.Equal(new RecordingStatistics(1, 1), coordinator.Status.Statistics);
    }

    [Fact]
    public async Task FailedRecordingIsExpectedButNotSaved()
    {
        var recorder = new FakeRecordingService
        {
            StartException = new InvalidOperationException("capture failed"),
        };
        await using var coordinator = CreateCoordinator(recorder);

        await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(new RecordingStatistics(1, 0), coordinator.Status.Statistics);
    }

    [Fact]
    public async Task MissingWowWindowReturnsTargetUnavailableWithoutRecorderFailure()
    {
        var recorder = new FakeRecordingService
        {
            StartException = new InvalidOperationException(
                "Recorder startup failed.",
                new CaptureTargetUnavailableException(
                    "Could not find a running World of Warcraft window."
                )
            ),
        };
        await using var coordinator = CreateCoordinator(recorder);

        var result = await coordinator.StartManualAsync(CancellationToken.None);

        Assert.Equal(RecordingCommandResult.TargetUnavailable, result);
        Assert.Null(coordinator.Status.LastFailure);
        Assert.Equal(new RecordingStatistics(0, 0), coordinator.Status.Statistics);
    }

    private static RecordingCoordinator CreateCoordinator(
        FakeRecordingService recorder,
        TimeSpan? startTimeout = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? disposeTimeout = null,
        RecordingCatalog? recordingCatalog = null
    )
    {
        return new RecordingCoordinator(
            recorder,
            NullLogger<RecordingCoordinator>.Instance,
            startTimeout ?? TimeSpan.FromSeconds(2),
            stopTimeout ?? TimeSpan.FromSeconds(2),
            disposeTimeout ?? TimeSpan.FromSeconds(2),
            recordingCatalog
        );
    }

    private static ChallengeRecordingContext Challenge()
    {
        return new ChallengeRecordingContext(
            DateTimeOffset.Now,
            "Magisters' Terrace",
            2811,
            558,
            22,
            [9, 10, 147]
        );
    }

    private static EncounterRecordingContext Encounter(int encounterId)
    {
        return new EncounterRecordingContext(
            DateTimeOffset.Now,
            encounterId,
            "Plexus Sentinel",
            WowDifficultyIds.MythicRaid
        );
    }

    private static async Task WaitForStateAsync(
        RecordingCoordinator coordinator,
        RecordingCoordinatorState expectedState
    )
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (coordinator.Status.State != expectedState)
        {
            Assert.True(
                DateTime.UtcNow < timeout,
                $"Coordinator did not reach state {expectedState}."
            );
            await Task.Delay(10);
        }
    }

    private sealed class TemporaryRecordingDatabase : IDisposable
    {
        private readonly TemporaryDirectory _directory;

        private TemporaryRecordingDatabase(
            TemporaryDirectory directory,
            SqliteConnectionFactory connectionFactory
        )
        {
            _directory = directory;
            Repository = new RecordingCatalogRepository(connectionFactory);
            Catalog = new RecordingCatalog(Repository);
        }

        public string DirectoryPath => _directory.Path;

        public RecordingCatalogRepository Repository { get; }

        public RecordingCatalog Catalog { get; }

        public static async Task<TemporaryRecordingDatabase> CreateAsync(
            CancellationToken cancellationToken
        )
        {
            var directory = new TemporaryDirectory();
            var databasePath = Path.Combine(directory.Path, "pullwatch.db");
            var factory = new SqliteConnectionFactory(
                new RecordingDatabasePathProvider(databasePath)
            );
            var initializer = new RecordingStorageInitializer(factory, NullLoggerFactory.Instance);

            try
            {
                await initializer.InitializeAsync(cancellationToken);
                return new TemporaryRecordingDatabase(directory, factory);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchCoordinatorTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
