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
    public void IdleRecorderReportsIdleHealth()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        Assert.Equal("Idle", viewModel.RecorderHealth);
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
    public void ListsSavedMp4RecordingsFromConfiguredDirectory()
    {
        var directory = CreateTempDirectory();

        try
        {
            var older = WriteRecording(directory, "older.mp4", "older", new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc));
            var newer = WriteRecording(directory, "newer.mp4", "newer", new DateTime(2026, 6, 15, 11, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "ignored");

            var viewModel = CreateViewModel(Status(
                RecordingCoordinatorState.Idle,
                recordingsDirectory: directory));

            Assert.Collection(
                viewModel.Recordings,
                first =>
                {
                    Assert.Equal(newer, first.Path);
                    Assert.Equal("newer", first.DisplayName);
                },
                second =>
                {
                    Assert.Equal(older, second.Path);
                    Assert.Equal("older", second.DisplayName);
                });
            Assert.Equal(newer, viewModel.SelectedRecording?.Path);
            Assert.Equal(string.Empty, viewModel.RecordingLibraryStatus);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ReportsMissingRecordingsDirectory()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        Assert.Empty(viewModel.Recordings);
        Assert.Null(viewModel.SelectedRecording);
        Assert.Equal(
            "Choose a recordings directory in settings to review videos here.",
            viewModel.RecordingLibraryStatus);
    }

    [Fact]
    public void SavedCountStatusChangeRefreshesRecordingsAndPreservesExistingSelection()
    {
        var directory = CreateTempDirectory();

        try
        {
            var older = WriteRecording(directory, "older.mp4", "older", new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc));
            WriteRecording(directory, "newer.mp4", "newer", new DateTime(2026, 6, 15, 11, 0, 0, DateTimeKind.Utc));
            var viewModel = CreateViewModel(Status(
                RecordingCoordinatorState.Idle,
                recordingsDirectory: directory));
            viewModel.SelectedRecording = viewModel.Recordings.Single(recording => recording.Path == older);

            WriteRecording(directory, "newest.mp4", "newest", new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));
            viewModel.ApplyStatus(Status(
                RecordingCoordinatorState.Idle,
                recordingsDirectory: directory,
                savedCount: 1));

            Assert.Equal(older, viewModel.SelectedRecording?.Path);
            Assert.Equal("newest", viewModel.Recordings[0].DisplayName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
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
        Exception? lastFailure = null,
        string? recordingsDirectory = null,
        int savedCount = 0)
    {
        RecordingOwner? owner = context switch
        {
            ManualRecordingContext => RecordingOwner.Manual,
            ChallengeRecordingContext => RecordingOwner.ChallengeMode,
            EncounterRecordingContext => RecordingOwner.Encounter,
            _ => null
        };

        return new ApplicationStatus(
            new PullWatchSettings
            {
                RecordingsDirectory = recordingsDirectory
            },
            new RecordingCoordinatorStatus(
                state,
                owner,
                null,
                context,
                null,
                null,
                lastFailure,
                state == RecordingCoordinatorState.Idle ? null : @"C:\Recordings\active.mp4")
            {
                Statistics = new RecordingStatistics(0, savedCount)
            },
            new CombatLogReaderStatus(
                CombatLogReaderState.WaitingForCombatLog,
                null,
                null,
                null));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PullWatch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteRecording(
        string directory,
        string fileName,
        string content,
        DateTime lastWriteTimeUtc)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        return path;
    }
}
