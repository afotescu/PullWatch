namespace PullWatch.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task DisplaysVideoOptionsAndAutosavesSelection()
    {
        var saves = new List<PullWatchSettings>();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return Saved(settings);
            }
        );

        Assert.Equal(VideoQuality.Balanced, viewModel.SelectedVideoQuality);
        Assert.Equal(VideoFrameRates.High, viewModel.SelectedFrameRate);
        Assert.Contains("1920x1080", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoQuality = VideoQuality.High;
        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        await WaitForAsync(() =>
            saves.Any(save =>
                save.Video.Quality == VideoQuality.High
                && save.Video.FrameRate == VideoFrameRates.Standard
            )
        );

        var saved = saves.Last();
        Assert.Equal(VideoQuality.High, saved.Video.Quality);
        Assert.Equal(VideoFrameRates.Standard, saved.Video.FrameRate);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
    }

    [Theory]
    [InlineData(RecordingCoordinatorState.Starting)]
    [InlineData(RecordingCoordinatorState.Recording)]
    [InlineData(RecordingCoordinatorState.Stopping)]
    public void DisablesEditingOutsideIdle(RecordingCoordinatorState state)
    {
        var viewModel = CreateViewModel(Status(state));

        Assert.False(viewModel.IsEditingEnabled);
        Assert.False(viewModel.PickWowLogsDirectoryCommand.CanExecute(null));
        Assert.False(viewModel.CommitWowLogsDirectoryCommand.CanExecute(null));
        Assert.Equal("Settings are locked while recording.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task ValidationFailureKeepsEditsAndShowsSaveError()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ =>
                Task.FromResult(
                    new SettingsSaveResult(
                        SettingsSaveStatus.Invalid,
                        null,
                        ["Video quality must be Compact, Balanced, or High."]
                    )
                )
        );

        viewModel.SelectedVideoQuality = VideoQuality.High;

        await WaitForAsync(() => viewModel.IsSaveError);

        Assert.Equal(VideoQuality.High, viewModel.SelectedVideoQuality);
        Assert.True(viewModel.IsSaveError);
        Assert.Equal("Fix the highlighted settings.", viewModel.SaveMessage);
    }

    [Fact]
    public void EstimateUpdatesWhenVideoOptionsChange()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            estimateCaptureSize: new VideoCaptureSize(2560, 1440)
        );

        Assert.Contains("910 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("60 FPS", viewModel.EstimatedRecordingSize);

        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        Assert.Contains("460 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("30 FPS", viewModel.EstimatedRecordingSize);
    }

    [Fact]
    public void OptionLabelsRemainStable()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        Assert.Equal(
            ["Compact", "Balanced", "High"],
            viewModel.VideoQualityOptions.Select(option => option.Label)
        );
        Assert.Equal(
            ["30 FPS", "60 FPS"],
            viewModel.FrameRateOptions.Select(option => option.Label)
        );
    }

    [Fact]
    public async Task UnexpectedSaveFailureIsDisplayed()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ =>
                Task.FromException<SettingsSaveResult>(
                    new InvalidOperationException("settings service unavailable")
                )
        );

        viewModel.RecordMythicPlus = false;

        await WaitForAsync(() => viewModel.IsSaveError);

        Assert.True(viewModel.IsSaveError);
        Assert.Equal(
            "Could not save settings: settings service unavailable",
            viewModel.SaveMessage
        );
    }

    [Fact]
    public async Task PathTypingDoesNotSaveUntilCommitted()
    {
        PullWatchSettings? saved = null;
        var path = Path.Combine(Path.GetTempPath(), "PullWatchLogs");
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saved = settings;
                return Saved(settings);
            }
        );

        viewModel.WowLogsDirectory = path;

        Assert.True(viewModel.IsWowLogsDirectoryPending);
        Assert.Null(saved);

        var committed = await viewModel.CommitWowLogsDirectoryAsync();

        Assert.True(committed);
        Assert.Equal(path, saved!.WowLogsDirectory);
        Assert.False(viewModel.IsWowLogsDirectoryPending);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task BrowseSelectionCommitsPathImmediately()
    {
        PullWatchSettings? saved = null;
        var selected = Path.Combine(Path.GetTempPath(), "PullWatchBrowseRecordings");
        var dialogs = new FakeSettingsDialogs { SelectedFolder = selected };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saved = settings;
                return Saved(settings);
            },
            dialogs: dialogs
        );

        await viewModel.PickRecordingsDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(selected, saved!.RecordingsDirectory);
        Assert.False(viewModel.IsRecordingsDirectoryPending);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task PathTypingClearsPreviousSuccessStatus()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        viewModel.RecordMythicPlus = false;
        await WaitForAsync(() => viewModel.SaveMessage == "Settings saved.");

        viewModel.WowLogsDirectory = Path.Combine(Path.GetTempPath(), "PullWatchPendingLogs");

        Assert.Null(viewModel.SaveMessage);
        Assert.True(viewModel.IsWowLogsDirectoryPending);
    }

    [Fact]
    public async Task DiscreteAutosaveUsesLastSavedPathWhilePathIsPending()
    {
        PullWatchSettings? saved = null;
        var savedLogsDirectory = Path.Combine(Path.GetTempPath(), "PullWatchSavedLogs");
        var pendingLogsDirectory = Path.Combine(Path.GetTempPath(), "PullWatchPendingLogs");
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                new PullWatchSettings { WowLogsDirectory = savedLogsDirectory }
            ),
            settings =>
            {
                saved = settings;
                return Saved(settings);
            }
        );

        viewModel.WowLogsDirectory = pendingLogsDirectory;
        viewModel.RecordMythicPlus = false;

        await WaitForAsync(() => saved?.RecordMythicPlus == false);

        Assert.Equal(savedLogsDirectory, saved!.WowLogsDirectory);
        Assert.Equal(pendingLogsDirectory, viewModel.WowLogsDirectory);
        Assert.True(viewModel.IsWowLogsDirectoryPending);
        Assert.Null(viewModel.SaveMessage);
    }

    [Fact]
    public async Task PathValidationFailureKeepsEditedValuePending()
    {
        var originalRecordingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "PullWatchOriginalRecordings"
        );
        var invalidRecordingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "PullWatchInvalidRecordings"
        );
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                new PullWatchSettings { RecordingsDirectory = originalRecordingsDirectory }
            ),
            _ =>
                Task.FromResult(
                    new SettingsSaveResult(
                        SettingsSaveStatus.Invalid,
                        null,
                        [
                            "Recordings directory is not writable: "
                                + invalidRecordingsDirectory
                                + ". Access denied.",
                        ]
                    )
                )
        );

        viewModel.RecordingsDirectory = invalidRecordingsDirectory;

        var committed = await viewModel.CommitRecordingsDirectoryAsync();

        Assert.False(committed);
        Assert.Equal(invalidRecordingsDirectory, viewModel.RecordingsDirectory);
        Assert.True(viewModel.IsRecordingsDirectoryPending);
        Assert.NotNull(viewModel.RecordingsDirectoryError);
        Assert.Equal("Fix the highlighted settings.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task FailedCommittedPathRetriesOnNextAutosave()
    {
        var saves = new List<PullWatchSettings>();
        var committedRecordingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "PullWatchRetriedRecordings"
        );
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);

                return saves.Count == 1
                    ? Task.FromResult(
                        new SettingsSaveResult(
                            SettingsSaveStatus.PersistenceFailed,
                            null,
                            [],
                            new IOException("settings file locked")
                        )
                    )
                    : Saved(settings);
            }
        );

        viewModel.RecordingsDirectory = committedRecordingsDirectory;

        var committed = await viewModel.CommitRecordingsDirectoryAsync();

        Assert.False(committed);
        Assert.True(viewModel.IsRecordingsDirectoryPending);
        Assert.Equal("Could not save settings: settings file locked", viewModel.SaveMessage);

        viewModel.RecordMythicPlus = false;

        await WaitForAsync(() => saves.Count == 2 && viewModel.SaveMessage == "Settings saved.");

        Assert.Equal(committedRecordingsDirectory, saves[1].RecordingsDirectory);
        Assert.False(saves[1].RecordMythicPlus);
        Assert.False(viewModel.IsRecordingsDirectoryPending);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task AutosavesAreSerializedAndCoalesced()
    {
        var firstSave = new TaskCompletionSource<SettingsSaveResult>();
        var saves = new List<PullWatchSettings>();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return saves.Count == 1 ? firstSave.Task : Saved(settings);
            }
        );

        viewModel.RecordMythicPlus = false;
        await WaitForAsync(() => saves.Count == 1);

        viewModel.CaptureCursor = false;
        firstSave.SetResult(new SettingsSaveResult(SettingsSaveStatus.Saved, saves[0], []));

        await WaitForAsync(() => saves.Count == 2);

        Assert.False(saves[1].RecordMythicPlus);
        Assert.False(saves[1].Video.CaptureCursor);
    }

    private static SettingsViewModel CreateViewModel(
        ApplicationStatus status,
        Func<PullWatchSettings, Task<SettingsSaveResult>>? save = null,
        VideoCaptureSize? estimateCaptureSize = null,
        ISettingsDialogs? dialogs = null
    )
    {
        return new SettingsViewModel(
            status,
            settings => save?.Invoke(settings) ?? Saved(settings),
            dialogs ?? new FakeSettingsDialogs(),
            () => estimateCaptureSize ?? new VideoCaptureSize(1920, 1080)
        );
    }

    private static Task<SettingsSaveResult> Saved(PullWatchSettings settings)
    {
        return Task.FromResult(new SettingsSaveResult(SettingsSaveStatus.Saved, settings, []));
    }

    private static ApplicationStatus Status(
        RecordingCoordinatorState state,
        PullWatchSettings? settings = null
    )
    {
        return new ApplicationStatus(
            settings
                ?? new PullWatchSettings
                {
                    RecordingsDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "PullWatchViewModelTests"
                    ),
                },
            new RecordingCoordinatorStatus(state, null, null, null, null, null, null, null),
            new CombatLogReaderStatus(CombatLogReaderState.WaitingForCombatLog, null, null, null),
            new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null)
        );
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class FakeSettingsDialogs : ISettingsDialogs
    {
        public string? SelectedFolder { get; init; }

        public string? PickFolder(string title, string? initialDirectory)
        {
            return SelectedFolder;
        }
    }
}
