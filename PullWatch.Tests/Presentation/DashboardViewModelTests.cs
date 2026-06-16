namespace PullWatch.Tests;

public sealed class DashboardViewModelTests
{
    [Theory]
    [InlineData(RecordingCoordinatorState.Idle, "Ready to record", true, false, "Manual start")]
    [InlineData(RecordingCoordinatorState.Starting, "Starting recording", false, false, "Manual start")]
    [InlineData(RecordingCoordinatorState.Recording, "Recording", true, true, "Manual stop")]
    [InlineData(RecordingCoordinatorState.Stopping, "Finalizing recording", false, false, "Manual start")]
    public void AppliesEveryRecordingState(
        RecordingCoordinatorState state,
        string expectedTitle,
        bool canRunManualCommand,
        bool isManualStopMode,
        string expectedManualButtonText)
    {
        var viewModel = CreateViewModel(Status(state));

        Assert.Equal(expectedTitle, viewModel.StateTitle);
        Assert.Equal(canRunManualCommand, viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.Equal(isManualStopMode, viewModel.IsManualStopMode);
        Assert.Equal(expectedManualButtonText, viewModel.ManualRecordingButtonText);
    }

    [Fact]
    public void PresentsAutomaticAndManualRecordingDetails()
    {
        var viewModel = CreateViewModel(Status(
            RecordingCoordinatorState.Recording,
            new ChallengeRecordingContext(DateTimeOffset.Now, "The Dawnbreaker", 12)));

        Assert.Equal("The Dawnbreaker · Mythic +12", viewModel.RecordingDetail);

        viewModel.ApplyStatus(Status(
            RecordingCoordinatorState.Recording,
            new EncounterRecordingContext(DateTimeOffset.Now, 123, "Plexus Sentinel", 16)));

        Assert.Equal("Plexus Sentinel · Raid encounter", viewModel.RecordingDetail);

        viewModel.ApplyStatus(Status(
            RecordingCoordinatorState.Recording,
            new ManualRecordingContext(DateTimeOffset.Now)));

        Assert.Equal("Manual recording", viewModel.RecordingDetail);
    }

    [Fact]
    public async Task ManualCommandDisablesDuringExecutionAndReportsResult()
    {
        var pendingStart = new TaskCompletionSource<RecordingCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => pendingStart.Task);

        var execution = viewModel.ManualRecordingCommand.ExecuteAsync();

        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));

        pendingStart.SetResult(RecordingCommandResult.Started);
        await execution;

        Assert.Equal("Manual recording started.", viewModel.CommandMessage);
        Assert.True(viewModel.ManualRecordingCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(RecordingCommandResult.AlreadyActive, "A recording is already active.")]
    [InlineData(RecordingCommandResult.TargetUnavailable, "World of Warcraft is not running.")]
    [InlineData(RecordingCommandResult.Failed, "The recording command failed.")]
    [InlineData(RecordingCommandResult.TimedOut, "The recorder did not respond in time.")]
    public async Task ManualCommandReportsNonSuccessResults(
        RecordingCommandResult result,
        string expectedMessage)
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => Task.FromResult(result));

        await viewModel.ManualRecordingCommand.ExecuteAsync();

        Assert.Equal(expectedMessage, viewModel.CommandMessage);
    }

    [Fact]
    public async Task UnexpectedManualCommandFailureIsDisplayed()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => Task.FromException<RecordingCommandResult>(
                new InvalidOperationException("controller unavailable")));

        await viewModel.ManualRecordingCommand.ExecuteAsync();

        Assert.Equal("Command failed: controller unavailable", viewModel.CommandMessage);
    }

    [Fact]
    public async Task FailedManualCommandUsesFailureDetailsWhenStatusArrives()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => Task.FromResult(RecordingCommandResult.Failed));

        await viewModel.ManualRecordingCommand.ExecuteAsync();
        viewModel.ApplyStatus(Status(
            RecordingCoordinatorState.Idle,
            lastFailure: new InvalidOperationException("encoder failed")));

        Assert.Equal("The recording command failed: encoder failed", viewModel.CommandMessage);
    }

    [Fact]
    public void FailureBannerPersistsUntilDismissedAndReturnsForNewFailure()
    {
        var firstFailure = new InvalidOperationException("capture failed");
        var viewModel = CreateViewModel(Status(
            RecordingCoordinatorState.Idle,
            lastFailure: firstFailure));

        Assert.True(viewModel.IsFailureVisible);
        Assert.Equal("capture failed", viewModel.FailureMessage);

        viewModel.DismissFailureCommand.Execute(null);
        viewModel.ApplyStatus(Status(
            RecordingCoordinatorState.Idle,
            lastFailure: firstFailure));

        Assert.False(viewModel.IsFailureVisible);

        viewModel.ApplyStatus(Status(
            RecordingCoordinatorState.Idle,
            lastFailure: new InvalidOperationException("finalization failed")));

        Assert.True(viewModel.IsFailureVisible);
        Assert.Equal("finalization failed", viewModel.FailureMessage);
        Assert.Equal("Ready to record", viewModel.StateTitle);
    }

    [Fact]
    public void IdleRecorderDoesNotClaimWowAvailability()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        Assert.Equal("Idle", viewModel.RecorderHealth);
        Assert.Equal(
            "Recording can start when World of Warcraft is running.",
            viewModel.RecorderDetail);
    }

    [Fact]
    public async Task WowWindowFailureIsShownAsCommandMessageNotRecorderFailure()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                lastFailure: new InvalidOperationException(
                    "Could not find a running World of Warcraft window.")),
            _ => Task.FromResult(RecordingCommandResult.Failed));

        await viewModel.ManualRecordingCommand.ExecuteAsync();

        Assert.Equal("World of Warcraft is not running.", viewModel.CommandMessage);
        Assert.Equal("Idle", viewModel.RecorderHealth);
        Assert.False(viewModel.IsFailureVisible);
        Assert.Null(viewModel.FailureMessage);
    }

    [Fact]
    public async Task ManualCommandStopsWhenRecording()
    {
        var stopCalls = 0;
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Recording, new ManualRecordingContext(DateTimeOffset.Now)),
            stopManual: _ =>
            {
                stopCalls++;
                return Task.FromResult(RecordingCommandResult.Stopped);
            });

        await viewModel.ManualRecordingCommand.ExecuteAsync();

        Assert.Equal(1, stopCalls);
        Assert.Equal("Recording stopped.", viewModel.CommandMessage);
    }

    [Fact]
    public void PresentsSessionRecordingStatistics()
    {
        var status = Status(RecordingCoordinatorState.Idle);
        var viewModel = CreateViewModel(status with
        {
            Recording = status.Recording with
            {
                Statistics = new RecordingStatistics(3, 2)
            }
        });

        Assert.Equal("3 expected · 2 saved this session", viewModel.RecordingStatistics);
    }

    [Theory]
    [InlineData(-1, "00:00:00")]
    [InlineData(65, "00:01:05")]
    [InlineData(3661, "01:01:01")]
    [InlineData(360005, "100:00:05")]
    public void FormatsDurationFromRecordingStart(int elapsedSeconds, string expected)
    {
        var startedAt = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var viewModel = CreateViewModel(Status(
            RecordingCoordinatorState.Recording,
            new ManualRecordingContext(startedAt)));

        viewModel.UpdateDuration(startedAt.AddSeconds(elapsedSeconds));

        Assert.Equal(expected, viewModel.Duration);
    }

    private static DashboardViewModel CreateViewModel(
        ApplicationStatus status,
        Func<CancellationToken, Task<RecordingCommandResult>>? startManual = null,
        Func<CancellationToken, Task<RecordingCommandResult>>? stopManual = null)
    {
        return new DashboardViewModel(
            status,
            startManual ?? (_ => Task.FromResult(RecordingCommandResult.Started)),
            stopManual ?? (_ => Task.FromResult(RecordingCommandResult.Stopped)),
            () => Task.CompletedTask);
    }

    private static ApplicationStatus Status(
        RecordingCoordinatorState state,
        RecordingContext? context = null,
        Exception? lastFailure = null)
    {
        RecordingOwner? owner = context switch
        {
            ManualRecordingContext => RecordingOwner.Manual,
            ChallengeRecordingContext => RecordingOwner.ChallengeMode,
            EncounterRecordingContext => RecordingOwner.Encounter,
            _ => null
        };

        return new ApplicationStatus(
            new PullWatchSettings(),
            new RecordingCoordinatorStatus(
                state,
                owner,
                null,
                context,
                null,
                null,
                lastFailure,
                state == RecordingCoordinatorState.Idle ? null : @"C:\Recordings\active.mp4"),
            new CombatLogReaderStatus(
                CombatLogReaderState.WaitingForCombatLog,
                null,
                null,
                null));
    }
}
