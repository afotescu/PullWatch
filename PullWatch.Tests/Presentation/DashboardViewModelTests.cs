namespace PullWatch.Tests;

public sealed class DashboardViewModelTests
{
    [Theory]
    [InlineData(RecordingCoordinatorState.Idle, "Ready to record", true, false)]
    [InlineData(RecordingCoordinatorState.Starting, "Starting recording", false, false)]
    [InlineData(RecordingCoordinatorState.Recording, "Recording", false, true)]
    [InlineData(RecordingCoordinatorState.Stopping, "Finalizing recording", false, false)]
    public void AppliesEveryRecordingState(
        RecordingCoordinatorState state,
        string expectedTitle,
        bool canStart,
        bool canStop)
    {
        var viewModel = CreateViewModel(Status(state));

        Assert.Equal(expectedTitle, viewModel.StateTitle);
        Assert.Equal(canStart, viewModel.StartManualCommand.CanExecute(null));
        Assert.Equal(canStop, viewModel.StopManualCommand.CanExecute(null));
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

        var execution = viewModel.StartManualCommand.ExecuteAsync();

        Assert.False(viewModel.StartManualCommand.CanExecute(null));

        pendingStart.SetResult(RecordingCommandResult.Started);
        await execution;

        Assert.Equal("Manual recording started.", viewModel.CommandMessage);
        Assert.True(viewModel.StartManualCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(RecordingCommandResult.AlreadyActive, "A recording is already active.")]
    [InlineData(RecordingCommandResult.Failed, "The recording command failed.")]
    [InlineData(RecordingCommandResult.TimedOut, "The recorder did not respond in time.")]
    public async Task ManualCommandReportsNonSuccessResults(
        RecordingCommandResult result,
        string expectedMessage)
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => Task.FromResult(result));

        await viewModel.StartManualCommand.ExecuteAsync();

        Assert.Equal(expectedMessage, viewModel.CommandMessage);
    }

    [Fact]
    public async Task UnexpectedManualCommandFailureIsDisplayed()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ => Task.FromException<RecordingCommandResult>(
                new InvalidOperationException("controller unavailable")));

        await viewModel.StartManualCommand.ExecuteAsync();

        Assert.Equal("Command failed: controller unavailable", viewModel.CommandMessage);
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
        Func<CancellationToken, Task<RecordingCommandResult>>? startManual = null)
    {
        return new DashboardViewModel(
            status,
            startManual ?? (_ => Task.FromResult(RecordingCommandResult.Started)),
            _ => Task.FromResult(RecordingCommandResult.Stopped),
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
