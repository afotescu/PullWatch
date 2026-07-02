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

        Assert.Equal(VideoCodec.H264, viewModel.SelectedVideoCodec);
        Assert.Equal(VideoQuality.Balanced, viewModel.SelectedVideoQuality);
        Assert.Equal(VideoFrameRates.High, viewModel.SelectedFrameRate);
        Assert.Equal(VideoScaling.Optimized, viewModel.SelectedVideoScaling);
        Assert.Contains("1920x1080", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoCodec = VideoCodec.H265;
        viewModel.SelectedVideoQuality = VideoQuality.High;
        viewModel.SelectedFrameRate = VideoFrameRates.Standard;
        viewModel.SelectedVideoScaling = VideoScaling.Original;

        await WaitForAsync(() =>
            saves.Any(save =>
                save.Video.Codec == VideoCodec.H265
                && save.Video.Quality == VideoQuality.High
                && save.Video.FrameRate == VideoFrameRates.Standard
                && save.Video.Scaling == VideoScaling.Original
            )
        );

        var saved = saves.Last();
        Assert.Equal(VideoCodec.H265, saved.Video.Codec);
        Assert.Equal(VideoQuality.High, saved.Video.Quality);
        Assert.Equal(VideoFrameRates.Standard, saved.Video.FrameRate);
        Assert.Equal(VideoScaling.Original, saved.Video.Scaling);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task RecordingStorageLimitAppliesExplicitlyAndDisplaysUsage()
    {
        const long bytesPerGigabyte = 1024L * 1024 * 1024;
        var saves = new List<PullWatchSettings>();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return Saved(settings);
            },
            initialRecordingStorageStatus: new RecordingStorageStatus(
                10 * bytesPerGigabyte,
                25 * bytesPerGigabyte,
                2,
                IsRefreshing: false,
                IsCleaning: false,
                LastDeletedRecordingCount: 0,
                LastError: null
            )
        );

        Assert.True(viewModel.IsRecordingStorageLimitEnabled);
        Assert.Equal(25, viewModel.RecordingStorageLimitGigabytes);
        Assert.Equal(25, viewModel.RecordingStorageLimitInputGigabytes);
        Assert.False(viewModel.CanApplyRecordingStorageLimit);
        Assert.False(viewModel.ApplyRecordingStorageLimitCommand.CanExecute(null));
        Assert.Equal(
            "Managed recordings storage: 10 GB / 25 GB",
            viewModel.RecordingStorageUsageText
        );

        viewModel.RecordingStorageLimitInputGigabytes = 30;

        Assert.Empty(saves);
        Assert.Equal(25, viewModel.RecordingStorageLimitGigabytes);
        Assert.True(viewModel.CanApplyRecordingStorageLimit);
        Assert.True(viewModel.ApplyRecordingStorageLimitCommand.CanExecute(null));
        Assert.Equal(
            "Managed recordings storage: 10 GB / 25 GB",
            viewModel.RecordingStorageUsageText
        );

        viewModel.ApplyRecordingStorageLimitCommand.Execute(null);

        await WaitForAsync(() =>
            saves.Any(save => save.Storage.MaxUsageBytes == 30 * bytesPerGigabyte)
        );

        Assert.Equal(30, viewModel.RecordingStorageLimitGigabytes);
        Assert.False(viewModel.CanApplyRecordingStorageLimit);
        Assert.False(viewModel.ApplyRecordingStorageLimitCommand.CanExecute(null));
        Assert.Equal(
            "Managed recordings storage: 10 GB / 30 GB",
            viewModel.RecordingStorageUsageText
        );

        viewModel.IsRecordingStorageLimitEnabled = false;

        await WaitForAsync(() =>
            saves.Any(save => save.Storage.MaxUsageBytes == RecordingStorageSettings.UnlimitedBytes)
        );

        Assert.Equal(
            "Managed recordings storage: 10 GB / Unlimited",
            viewModel.RecordingStorageUsageText
        );
    }

    [Fact]
    public async Task PendingRecordingStorageLimitApplyChoiceSavesAndContinuesNavigation()
    {
        const long bytesPerGigabyte = 1024L * 1024 * 1024;
        var saves = new List<PullWatchSettings>();
        var dialogs = new FakeSettingsDialogs
        {
            PendingRecordingStorageLimitChangeAction =
                PendingRecordingStorageLimitChangeAction.Apply,
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return Saved(settings);
            },
            dialogs: dialogs
        );

        viewModel.RecordingStorageLimitInputGigabytes = 30;

        var canLeaveSettings = viewModel.ConfirmPendingRecordingStorageLimitChangeForNavigation();

        Assert.True(canLeaveSettings);
        Assert.Equal([(25, 30)], dialogs.PendingRecordingStorageLimitChangeRequests);
        Assert.Equal(30, viewModel.RecordingStorageLimitGigabytes);
        Assert.False(viewModel.HasPendingRecordingStorageLimitChange);
        await WaitForAsync(() =>
            saves.Any(save => save.Storage.MaxUsageBytes == 30 * bytesPerGigabyte)
        );
    }

    [Fact]
    public void PendingRecordingStorageLimitDiscardChoiceResetsDraftAndContinuesNavigation()
    {
        var saves = new List<PullWatchSettings>();
        var dialogs = new FakeSettingsDialogs
        {
            PendingRecordingStorageLimitChangeAction =
                PendingRecordingStorageLimitChangeAction.Discard,
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return Saved(settings);
            },
            dialogs: dialogs
        );

        viewModel.RecordingStorageLimitInputGigabytes = 30;

        var canLeaveSettings = viewModel.ConfirmPendingRecordingStorageLimitChangeForNavigation();

        Assert.True(canLeaveSettings);
        Assert.Equal([(25, 30)], dialogs.PendingRecordingStorageLimitChangeRequests);
        Assert.Empty(saves);
        Assert.Equal(25, viewModel.RecordingStorageLimitGigabytes);
        Assert.Equal(25, viewModel.RecordingStorageLimitInputGigabytes);
        Assert.False(viewModel.HasPendingRecordingStorageLimitChange);
    }

    [Fact]
    public void PendingRecordingStorageLimitCancelChoiceKeepsDraftAndCancelsNavigation()
    {
        var saves = new List<PullWatchSettings>();
        var dialogs = new FakeSettingsDialogs
        {
            PendingRecordingStorageLimitChangeAction =
                PendingRecordingStorageLimitChangeAction.Cancel,
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return Saved(settings);
            },
            dialogs: dialogs
        );

        viewModel.RecordingStorageLimitInputGigabytes = 30;

        var canLeaveSettings = viewModel.ConfirmPendingRecordingStorageLimitChangeForNavigation();

        Assert.False(canLeaveSettings);
        Assert.Equal([(25, 30)], dialogs.PendingRecordingStorageLimitChangeRequests);
        Assert.Empty(saves);
        Assert.Equal(25, viewModel.RecordingStorageLimitGigabytes);
        Assert.Equal(30, viewModel.RecordingStorageLimitInputGigabytes);
        Assert.True(viewModel.HasPendingRecordingStorageLimitChange);
    }

    [Fact]
    public async Task RecordingFilterOptionsAutosave()
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

        Assert.True(viewModel.CanConfigureMythicPlus);
        Assert.True(viewModel.CanConfigureRaidEncounters);
        Assert.Equal(0, viewModel.MinimumMythicPlusKeystoneLevel);
        Assert.True(viewModel.RecordRaidFinder);
        Assert.True(viewModel.RecordNormalRaid);
        Assert.True(viewModel.RecordHeroicRaid);
        Assert.True(viewModel.RecordMythicRaid);

        viewModel.MinimumMythicPlusKeystoneLevel = 12;
        viewModel.RecordRaidFinder = false;
        viewModel.RecordMythicRaid = false;

        await WaitForAsync(() =>
            saves.Any(save =>
                save.RecordingFilters.MythicPlus.MinimumKeystoneLevel == 12
                && !save.RecordingFilters.RaidEncounters.RecordRaidFinder
                && !save.RecordingFilters.RaidEncounters.RecordMythic
            )
        );

        var saved = saves.Last();
        Assert.Equal(12, saved.RecordingFilters.MythicPlus.MinimumKeystoneLevel);
        Assert.False(saved.RecordingFilters.RaidEncounters.RecordRaidFinder);
        Assert.True(saved.RecordingFilters.RaidEncounters.RecordNormal);
        Assert.True(saved.RecordingFilters.RaidEncounters.RecordHeroic);
        Assert.False(saved.RecordingFilters.RaidEncounters.RecordMythic);
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
        Assert.False(viewModel.ApplyRecordingStorageLimitCommand.CanExecute(null));
        Assert.False(viewModel.CanConfigureMythicPlus);
        Assert.False(viewModel.CanConfigureRaidEncounters);
        Assert.False(viewModel.CanStartMinimizedToTray);
        Assert.Equal("Settings are locked while recording.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task StartupOptionsAutosaveAndSyncShortcut()
    {
        var saves = new List<PullWatchSettings>();
        var shortcut = new FakeWindowsStartupShortcut();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            settings =>
            {
                saves.Add(settings);
                return Saved(settings);
            },
            windowsStartupShortcut: shortcut
        );

        Assert.False(viewModel.CanStartMinimizedToTray);

        viewModel.StartWithWindows = true;

        await WaitForAsync(() => shortcut.SyncedSettings.Count == 1);

        Assert.True(saves.Last().Startup.StartWithWindows);
        Assert.False(saves.Last().Startup.StartMinimizedToTray);
        Assert.True(viewModel.CanStartMinimizedToTray);
        Assert.Equal(saves.Last().Startup, shortcut.SyncedSettings.Last());

        viewModel.StartMinimizedToTray = true;

        await WaitForAsync(() =>
            shortcut.SyncedSettings.Count == 2
            && shortcut.SyncedSettings.Last().StartMinimizedToTray
        );

        Assert.True(saves.Last().Startup.StartWithWindows);
        Assert.True(saves.Last().Startup.StartMinimizedToTray);
        Assert.Equal(saves.Last().Startup, shortcut.SyncedSettings.Last());
    }

    [Fact]
    public async Task DisablingWindowsStartupClearsMinimizedToTray()
    {
        var shortcut = new FakeWindowsStartupShortcut();
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                new PullWatchSettings
                {
                    Startup = new StartupSettings
                    {
                        StartWithWindows = true,
                        StartMinimizedToTray = true,
                    },
                }
            ),
            windowsStartupShortcut: shortcut
        );

        viewModel.StartWithWindows = false;

        await WaitForAsync(() => shortcut.SyncedSettings.Count == 1);

        Assert.False(viewModel.StartWithWindows);
        Assert.False(viewModel.StartMinimizedToTray);
        Assert.False(viewModel.CanStartMinimizedToTray);
        Assert.False(shortcut.SyncedSettings.Last().StartWithWindows);
        Assert.False(shortcut.SyncedSettings.Last().StartMinimizedToTray);
    }

    [Fact]
    public async Task StartupShortcutFailureIsDisplayed()
    {
        var shortcut = new FakeWindowsStartupShortcut
        {
            Exception = new IOException("startup folder denied"),
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            windowsStartupShortcut: shortcut
        );

        viewModel.StartWithWindows = true;

        await WaitForAsync(() => viewModel.IsSaveError);

        Assert.Equal(
            "Settings saved, but Windows startup could not be updated: startup folder denied",
            viewModel.SaveMessage
        );
    }

    [Fact]
    public async Task StartupShortcutFailureRetriesOnNextAutosave()
    {
        var shortcut = new FakeWindowsStartupShortcut
        {
            Exception = new IOException("startup folder denied"),
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            windowsStartupShortcut: shortcut
        );

        viewModel.StartWithWindows = true;
        await WaitForAsync(() => shortcut.SyncedSettings.Count == 1 && viewModel.IsSaveError);

        shortcut.Exception = null;
        viewModel.CaptureCursor = false;

        await WaitForAsync(() => shortcut.SyncedSettings.Count == 2 && !viewModel.IsSaveError);

        Assert.True(shortcut.SyncedSettings.Last().StartWithWindows);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
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

        Assert.Contains("90 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("1920x1080", viewModel.EstimatedRecordingSize);
        Assert.Contains("2560x1440", viewModel.EstimatedRecordingSize);
        Assert.Contains("H.264", viewModel.EstimatedRecordingSize);
        Assert.Contains("60 FPS", viewModel.EstimatedRecordingSize);
        Assert.Contains("12 Mbps target", viewModel.EstimatedRecordingSize);
        Assert.Contains("per minute", viewModel.EstimatedRecordingSize);
        Assert.Contains("WoW window size", viewModel.EstimatedRecordingSize);

        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        Assert.Contains("50 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("30 FPS", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoScaling = VideoScaling.Original;

        Assert.Contains("90 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("2560x1440", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoCodec = VideoCodec.H265;

        Assert.Contains("60 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("H.265", viewModel.EstimatedRecordingSize);
        Assert.Contains("8 Mbps estimate", viewModel.EstimatedRecordingSize);
    }

    [Fact]
    public void OptionLabelsRemainStable()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            estimateCaptureSize: new VideoCaptureSize(2560, 1440)
        );

        Assert.Equal(
            ["H.264 / AVC", "H.265 / HEVC"],
            viewModel.VideoCodecOptions.Select(option => option.Label)
        );
        Assert.Equal(
            ["Compact", "Balanced", "High"],
            viewModel.VideoQualityOptions.Select(option => option.Label)
        );
        Assert.Equal(
            ["30 FPS", "60 FPS"],
            viewModel.FrameRateOptions.Select(option => option.Label)
        );
        Assert.Equal(
            [
                "Original (2560x1440 estimated)",
                "1080p (1920x1080 estimated)",
                "720p (1280x720 estimated)",
            ],
            viewModel.VideoScalingOptions.Select(option => option.Label)
        );
    }

    [Fact]
    public void SelectedNoOpScalingOptionRemainsVisible()
    {
        var settings = new PullWatchSettings
        {
            RecordingsDirectory = Path.Combine(Path.GetTempPath(), "PullWatchViewModelTests"),
            Video = new VideoSettings { Scaling = VideoScaling.Target1440p },
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, settings),
            estimateCaptureSize: new VideoCaptureSize(2560, 1440)
        );

        Assert.Equal(
            [
                "Original (2560x1440 estimated)",
                "1440p (2560x1440 estimated)",
                "1080p (1920x1080 estimated)",
                "720p (1280x720 estimated)",
            ],
            viewModel.VideoScalingOptions.Select(option => option.Label)
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
        ISettingsDialogs? dialogs = null,
        IWindowsStartupShortcut? windowsStartupShortcut = null,
        RecordingStorageStatus? initialRecordingStorageStatus = null
    )
    {
        return new SettingsViewModel(
            status,
            settings => save?.Invoke(settings) ?? Saved(settings),
            dialogs ?? new FakeSettingsDialogs(),
            () => estimateCaptureSize ?? new VideoCaptureSize(1920, 1080),
            windowsStartupShortcut,
            initialRecordingStorageStatus
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
            new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null, null)
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

        public PendingRecordingStorageLimitChangeAction PendingRecordingStorageLimitChangeAction { get; init; } =
            PendingRecordingStorageLimitChangeAction.Cancel;

        public List<(
            int CurrentGigabytes,
            int PendingGigabytes
        )> PendingRecordingStorageLimitChangeRequests { get; } = [];

        public string? PickFolder(string title, string? initialDirectory)
        {
            return SelectedFolder;
        }

        public PendingRecordingStorageLimitChangeAction ConfirmPendingRecordingStorageLimitChange(
            int currentGigabytes,
            int pendingGigabytes
        )
        {
            PendingRecordingStorageLimitChangeRequests.Add((currentGigabytes, pendingGigabytes));
            return PendingRecordingStorageLimitChangeAction;
        }
    }

    private sealed class FakeWindowsStartupShortcut : IWindowsStartupShortcut
    {
        public List<StartupSettings> SyncedSettings { get; } = [];

        public Exception? Exception { get; set; }

        public Task SyncAsync(StartupSettings settings)
        {
            SyncedSettings.Add(settings);

            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }
}
