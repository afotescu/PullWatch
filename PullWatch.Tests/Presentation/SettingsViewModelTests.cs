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

        Assert.Null(viewModel.SelectedVideoProfile);
        Assert.Equal("Not tested", viewModel.VideoEncodingSummary);
        Assert.Equal(
            "PullWatch needs to test video encoding before recording.",
            viewModel.VideoEncodingStatus
        );
        Assert.Empty(viewModel.VideoProfileOptions);
        Assert.False(viewModel.HasVideoProfileOptions);
        Assert.False(viewModel.CanChooseVideoProfile);
        Assert.Equal("Test video encoding", viewModel.TestVideoEncodingButtonText);
        Assert.Equal(VideoQuality.Balanced, viewModel.SelectedVideoQuality);
        Assert.Equal(VideoFrameRates.High, viewModel.SelectedFrameRate);
        Assert.Equal(VideoScaling.Optimized, viewModel.SelectedVideoScaling);
        Assert.Contains("1920x1080", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoQuality = VideoQuality.High;
        viewModel.SelectedFrameRate = VideoFrameRates.Standard;
        viewModel.SelectedVideoScaling = VideoScaling.Original;

        await WaitForAsync(() =>
            saves.Any(save =>
                save.Video.Quality == VideoQuality.High
                && save.Video.FrameRate == VideoFrameRates.Standard
                && save.Video.Scaling == VideoScaling.Original
            )
        );

        var saved = saves.Last();
        Assert.Null(saved.Video.SelectedProfile);
        Assert.Equal(VideoQuality.High, saved.Video.Quality);
        Assert.Equal(VideoFrameRates.Standard, saved.Video.FrameRate);
        Assert.Equal(VideoScaling.Original, saved.Video.Scaling);
        Assert.Equal("Settings saved.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task VideoProfileOptionsUsePassingCalibrationResultsAndAutosaveSelection()
    {
        var saves = new List<PullWatchSettings>();
        var selectedProfile = Profile(VideoCodec.H265, VideoEncoderProvider.NvidiaNvenc);
        var settings = new PullWatchSettings
        {
            Video = new VideoSettings { SelectedProfile = selectedProfile },
            EncoderCalibration = new EncoderCalibrationSettings
            {
                Results =
                [
                    CalibrationResult(VideoCodec.H264, VideoEncoderProvider.Software, passed: true),
                    CalibrationResult(
                        VideoCodec.H265,
                        VideoEncoderProvider.NvidiaNvenc,
                        passed: true
                    ),
                    CalibrationResult(
                        VideoCodec.H265,
                        VideoEncoderProvider.Software,
                        passed: false
                    ),
                    CalibrationResult(VideoCodec.H264, VideoEncoderProvider.AmdAmf, passed: true),
                ],
            },
        };
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, settings),
            save: savedSettings =>
            {
                saves.Add(savedSettings);
                return Saved(savedSettings);
            }
        );

        Assert.Equal(selectedProfile, viewModel.SelectedVideoProfile);
        Assert.Equal("H.265 / NVIDIA NVENC", viewModel.VideoEncodingSummary);
        Assert.Equal("Ready for recording", viewModel.VideoEncodingStatus);
        Assert.True(viewModel.HasVideoProfileOptions);
        Assert.True(viewModel.CanChooseVideoProfile);
        Assert.Equal(
            [
                Profile(VideoCodec.H265, VideoEncoderProvider.NvidiaNvenc),
                Profile(VideoCodec.H264, VideoEncoderProvider.AmdAmf),
                Profile(VideoCodec.H264, VideoEncoderProvider.Software),
            ],
            viewModel.VideoProfileOptions.Select(option => option.Value)
        );
        Assert.Equal(
            ["H.265 / NVIDIA NVENC", "H.264 / AMD AMF", "H.264 / Software"],
            viewModel.VideoProfileOptions.Select(option => option.Label)
        );

        viewModel.SelectedVideoProfile = Profile(VideoCodec.H265, VideoEncoderProvider.Software);

        Assert.Equal(selectedProfile, viewModel.SelectedVideoProfile);
        Assert.Empty(saves);

        viewModel.SelectedVideoProfile = Profile(VideoCodec.H264, VideoEncoderProvider.AmdAmf);

        await WaitForAsync(() => saves.Count == 1);

        Assert.Equal(
            Profile(VideoCodec.H264, VideoEncoderProvider.AmdAmf),
            saves[0].Video.SelectedProfile
        );
        Assert.Equal("H.264 / AMD AMF", viewModel.VideoEncodingSummary);
    }

    [Fact]
    public async Task TestVideoEncodingCommandRunsConfiguredAction()
    {
        var testStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var testCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            testVideoEncoding: async () =>
            {
                testStarted.SetResult();
                await testCompleted.Task;
            }
        );

        var testTask = viewModel.TestVideoEncodingAsync();
        await testStarted.Task;

        Assert.True(viewModel.IsTestingVideoEncoding);
        Assert.False(viewModel.CanTestVideoEncoding);
        Assert.Equal("Testing video encoding...", viewModel.TestVideoEncodingButtonText);

        testCompleted.SetResult();
        await testTask;

        Assert.False(viewModel.IsTestingVideoEncoding);
        Assert.True(viewModel.CanTestVideoEncoding);
        Assert.Equal("Test video encoding", viewModel.TestVideoEncodingButtonText);
    }

    [Fact]
    public async Task TestVideoEncodingCommandReportsFailure()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            testVideoEncoding: () => throw new InvalidOperationException("FFmpeg is missing.")
        );

        await viewModel.TestVideoEncodingAsync();

        Assert.True(viewModel.IsSaveError);
        Assert.Equal("Video encoding test failed: FFmpeg is missing.", viewModel.SaveMessage);
    }

    [Fact]
    public async Task SuccessfulSaveShowsTemporaryNotificationWithoutDuplicates()
    {
        var notifications = new NotificationCenterViewModel();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            notifications: notifications,
            settingsSuccessNotificationDuration: TimeSpan.FromMilliseconds(50)
        );

        viewModel.SelectedVideoQuality = VideoQuality.High;

        await WaitForAsync(() => notifications.HasNotifications);

        var notification = Assert.Single(notifications.Items);
        Assert.Equal(NotificationSeverity.Success, notification.Severity);
        Assert.Equal("Settings saved.", notification.Title);

        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        await WaitForAsync(() => viewModel.SelectedFrameRate == VideoFrameRates.Standard);

        Assert.Single(notifications.Items);

        await WaitForAsync(() => !notifications.HasNotifications);
    }

    [Fact]
    public async Task SaveErrorNotificationOverwritesVisibleSuccessNotification()
    {
        var saveCount = 0;
        var notifications = new NotificationCenterViewModel();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            _ =>
            {
                saveCount++;
                return saveCount == 1
                    ? Saved(new PullWatchSettings())
                    : Task.FromResult(
                        new SettingsSaveResult(
                            SettingsSaveStatus.Invalid,
                            null,
                            ["Video codec must be H.264 or H.265."]
                        )
                    );
            },
            notifications: notifications
        );

        viewModel.SelectedVideoQuality = VideoQuality.High;
        await WaitForAsync(() => notifications.HasNotifications);

        Assert.Equal(NotificationSeverity.Success, Assert.Single(notifications.Items).Severity);

        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        await WaitForAsync(() =>
            notifications.Items.Count == 1
            && notifications.Items[0].Severity == NotificationSeverity.Error
        );

        var notification = Assert.Single(notifications.Items);
        Assert.Equal("Settings need attention", notification.Title);
        Assert.Equal("Fix the highlighted settings.", notification.Message);
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

        Assert.Contains("70 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("1920x1080", viewModel.EstimatedRecordingSize);
        Assert.Contains("2560x1440", viewModel.EstimatedRecordingSize);
        Assert.Contains("H.264", viewModel.EstimatedRecordingSize);
        Assert.Contains("60 FPS", viewModel.EstimatedRecordingSize);
        Assert.Contains("9 Mbps target", viewModel.EstimatedRecordingSize);
        Assert.Contains("per minute", viewModel.EstimatedRecordingSize);
        Assert.Contains("WoW window size", viewModel.EstimatedRecordingSize);

        viewModel.SelectedFrameRate = VideoFrameRates.Standard;

        Assert.Contains("30 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("30 FPS", viewModel.EstimatedRecordingSize);

        viewModel.SelectedVideoScaling = VideoScaling.Original;

        Assert.Contains("60 MB", viewModel.EstimatedRecordingSize);
        Assert.Contains("2560x1440", viewModel.EstimatedRecordingSize);

        var h265ViewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                new PullWatchSettings
                {
                    RecordingsDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "PullWatchViewModelTests"
                    ),
                    Video = new VideoSettings
                    {
                        SelectedProfile = Profile(
                            VideoCodec.H265,
                            VideoEncoderProvider.NvidiaNvenc
                        ),
                        FrameRate = VideoFrameRates.Standard,
                        Scaling = VideoScaling.Original,
                    },
                    EncoderCalibration = new EncoderCalibrationSettings
                    {
                        Results =
                        [
                            CalibrationResult(
                                VideoCodec.H265,
                                VideoEncoderProvider.NvidiaNvenc,
                                passed: true
                            ),
                        ],
                    },
                }
            ),
            estimateCaptureSize: new VideoCaptureSize(2560, 1440)
        );

        Assert.Contains("30 MB", h265ViewModel.EstimatedRecordingSize);
        Assert.Contains("H.265", h265ViewModel.EstimatedRecordingSize);
        Assert.Contains("4 Mbps target", h265ViewModel.EstimatedRecordingSize);
    }

    [Fact]
    public void OptionLabelsRemainStable()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            estimateCaptureSize: new VideoCaptureSize(2560, 1440)
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
        Func<Task>? testVideoEncoding = null,
        IWindowsStartupShortcut? windowsStartupShortcut = null,
        RecordingStorageStatus? initialRecordingStorageStatus = null,
        NotificationCenterViewModel? notifications = null,
        TimeSpan? settingsSuccessNotificationDuration = null
    )
    {
        return new SettingsViewModel(
            status,
            settings => save?.Invoke(settings) ?? Saved(settings),
            dialogs ?? new FakeSettingsDialogs(),
            getEstimateCaptureSize: () => estimateCaptureSize ?? new VideoCaptureSize(1920, 1080),
            testVideoEncoding: testVideoEncoding,
            windowsStartupShortcut: windowsStartupShortcut,
            initialRecordingStorageStatus: initialRecordingStorageStatus,
            notifications: notifications,
            notificationDispatcher: ImmediateUiDispatcher.Instance,
            settingsSuccessNotificationDuration: settingsSuccessNotificationDuration
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

    private static VideoProfileSelection Profile(VideoCodec codec, VideoEncoderProvider provider)
    {
        return new VideoProfileSelection { Codec = codec, Provider = provider };
    }

    private static EncoderCalibrationResult CalibrationResult(
        VideoCodec codec,
        VideoEncoderProvider provider,
        bool passed
    )
    {
        return new EncoderCalibrationResult
        {
            Codec = codec,
            Provider = provider,
            EncoderName = $"{codec}-{provider}",
            Passed = passed,
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public void Post(Action action)
        {
            action();
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
