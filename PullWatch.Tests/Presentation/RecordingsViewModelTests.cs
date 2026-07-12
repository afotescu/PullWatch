namespace PullWatch.Tests;

public sealed class RecordingsViewModelTests
{
    private static readonly EncoderCalibrationEnvironment CurrentEncoderEnvironment = new(
        @"C:\ffmpeg\bin\ffmpeg.exe",
        "ffmpeg version test",
        "ABC123"
    );

    [Theory]
    [InlineData(
        RecordingCoordinatorState.Idle,
        "Ready",
        true,
        false,
        "Manual start",
        "Idle. Click to start manual recording."
    )]
    [InlineData(
        RecordingCoordinatorState.Starting,
        "Processing",
        false,
        false,
        "Manual start",
        "WoW is running.\r\nStarting recording."
    )]
    [InlineData(
        RecordingCoordinatorState.Recording,
        "Recording",
        true,
        true,
        "Manual stop",
        "Recording. Click to stop."
    )]
    [InlineData(
        RecordingCoordinatorState.Stopping,
        "Processing",
        false,
        false,
        "Manual start",
        "WoW recording is being saved."
    )]
    public void AppliesEveryRecordingState(
        RecordingCoordinatorState state,
        string expectedTitle,
        bool canRunManualCommand,
        bool isManualStopMode,
        string expectedManualButtonText,
        string expectedCollapsedStatusToolTip
    )
    {
        var viewModel = CreateViewModel(Status(state));

        Assert.Equal(expectedTitle, viewModel.StateTitle);
        Assert.Equal(canRunManualCommand, viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.Equal(canRunManualCommand, viewModel.StatusCardCommand.CanExecute(null));
        Assert.Equal(isManualStopMode, viewModel.IsManualStopMode);
        Assert.Equal(expectedManualButtonText, viewModel.ManualRecordingButtonText);
        Assert.Equal(expectedManualButtonText, viewModel.StatusCardButtonText);
        Assert.Equal(
            expectedCollapsedStatusToolTip.Replace("\r\n", Environment.NewLine),
            viewModel.CollapsedStatusToolTip
        );
    }

    [Fact]
    public async Task RestoresAndPersistsPlaybackAudioState()
    {
        var saves = new List<(int VolumePercent, bool IsMuted)>();
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                playbackVolumePercent: 35,
                isPlaybackMuted: true
            ),
            savePlaybackAudioState: (volumePercent, isMuted) =>
            {
                saves.Add((volumePercent, isMuted));
                return Task.FromResult(true);
            }
        );

        Assert.Equal(35, viewModel.PlaybackVolumePercent);
        Assert.True(viewModel.IsPlaybackMuted);

        await viewModel.UpdatePlaybackAudioStateAsync(70, isMuted: false);

        Assert.Equal(70, viewModel.PlaybackVolumePercent);
        Assert.False(viewModel.IsPlaybackMuted);
        Assert.Equal([(70, false)], saves);

        await viewModel.UpdatePlaybackAudioStateAsync(0, isMuted: false);

        Assert.Equal(0, viewModel.PlaybackVolumePercent);
        Assert.True(viewModel.IsPlaybackMuted);
        Assert.Equal([(70, false), (0, true)], saves);

        await viewModel.UpdatePlaybackAudioStateAsync(0, isMuted: true);

        Assert.Equal(2, saves.Count);
    }

    [Fact]
    public async Task FailedPlaybackAudioSaveCanRetryTheSameState()
    {
        var saveAttempts = 0;
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            savePlaybackAudioState: (_, _) => Task.FromResult(++saveAttempts > 1)
        );

        await viewModel.UpdatePlaybackAudioStateAsync(35, isMuted: false);
        await viewModel.UpdatePlaybackAudioStateAsync(35, isMuted: false);

        Assert.Equal(2, saveAttempts);
        Assert.Equal(35, viewModel.PlaybackVolumePercent);
        Assert.False(viewModel.IsPlaybackMuted);
    }

    [Fact]
    public async Task MissingVideoEncodingSetupReplacesManualActionWithSetupAction()
    {
        var testCalls = 0;
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, includeSelectedVideoProfile: false),
            testVideoEncoding: () =>
            {
                testCalls++;
                return Task.CompletedTask;
            }
        );

        Assert.Equal("Setup needed", viewModel.StateTitle);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Video encoding needs to be tested before recording.",
                "Manual and automatic recording stay disabled until setup is complete."
            ),
            viewModel.ReadinessDetail
        );
        Assert.Equal(RecordingStatusHealth.AttentionNeeded, viewModel.StatusHealth);
        Assert.True(viewModel.IsVideoEncodingSetupRequired);
        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.True(viewModel.StatusCardCommand.CanExecute(null));
        Assert.Equal("Test video encoding", viewModel.StatusCardButtonText);
        Assert.Equal("Test video encoding before recording.", viewModel.CollapsedStatusToolTip);

        await viewModel.StatusCardCommand.ExecuteAsync(null);

        Assert.Equal(1, testCalls);
        Assert.Null(viewModel.CommandMessage);
    }

    [Fact]
    public void MissingCalibrationResultsReplaceManualActionWithSetupAction()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                includeSelectedVideoProfile: true,
                includeEncoderCalibrationResults: false
            )
        );

        Assert.Equal("Setup needed", viewModel.StateTitle);
        Assert.Equal(RecordingStatusHealth.AttentionNeeded, viewModel.StatusHealth);
        Assert.True(viewModel.IsVideoEncodingSetupRequired);
        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.True(viewModel.StatusCardCommand.CanExecute(null));
        Assert.Equal("Test video encoding", viewModel.StatusCardButtonText);
    }

    [Fact]
    public void StaleCalibrationVersionReplacesManualActionWithSetupAction()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                encoderCalibrationVersion: EncoderCalibrationSettings.CurrentVersion + 1
            )
        );

        Assert.Equal("Setup needed", viewModel.StateTitle);
        Assert.Contains("retested after an app update", viewModel.ReadinessDetail);
        Assert.True(viewModel.IsVideoEncodingSetupRequired);
        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.True(viewModel.StatusCardCommand.CanExecute(null));
    }

    [Fact]
    public void CalibrationFailureReplacesManualActionWithSetupActionWithoutFailureNotification()
    {
        var notifications = new NotificationCenterViewModel();
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                lastFailure: new InvalidOperationException(
                    "Video encoding needs to be retested because the FFmpeg path changed."
                )
            ),
            notifications: notifications
        );

        Assert.Equal("Setup needed", viewModel.StateTitle);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Video encoding needs to be retested because the FFmpeg path changed.",
                "Manual and automatic recording stay disabled until setup is complete."
            ),
            viewModel.ReadinessDetail
        );
        Assert.True(viewModel.IsVideoEncodingSetupRequired);
        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.True(viewModel.StatusCardCommand.CanExecute(null));
        Assert.False(viewModel.IsFailureVisible);
        Assert.Null(viewModel.FailureMessage);
        Assert.Empty(notifications.Items);
    }

    [Fact]
    public async Task VideoEncodingSetupActionReportsFailure()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, includeSelectedVideoProfile: false),
            testVideoEncoding: () => throw new InvalidOperationException("FFmpeg is missing.")
        );

        await viewModel.StatusCardCommand.ExecuteAsync(null);

        Assert.Equal("Video encoding test failed: FFmpeg is missing.", viewModel.CommandMessage);
    }

    [Fact]
    public void ReadingCombatLogReportsAutomaticReadiness()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                combatLogState: CombatLogReaderState.ReadingCombatLog,
                combatLogPath: @"C:\WoW\Logs\WoWCombatLog-current.txt"
            )
        );

        Assert.Equal("Ready", viewModel.StateTitle);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "WoW is running.",
                "Manual and automatic recording are ready."
            ),
            viewModel.ReadinessDetail
        );
        Assert.Equal(RecordingStatusHealth.Ready, viewModel.StatusHealth);
        Assert.True(viewModel.ManualRecordingCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(CombatLogReaderState.WaitingForLogsDirectory)]
    [InlineData(CombatLogReaderState.WaitingForCombatLog)]
    public void MissingCombatLogPrerequisitesReportManualOnly(CombatLogReaderState combatLogState)
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, combatLogState: combatLogState)
        );

        Assert.Equal("Ready", viewModel.StateTitle);
        Assert.StartsWith(
            string.Join(Environment.NewLine, "WoW is running.", "Manual recording is ready."),
            viewModel.ReadinessDetail
        );
        Assert.Equal(RecordingStatusHealth.ManualOnly, viewModel.StatusHealth);
        Assert.True(viewModel.ManualRecordingCommand.CanExecute(null));
    }

    [Fact]
    public void CombatLogErrorReportsAttentionWithoutDisablingManualRecording()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                combatLogState: CombatLogReaderState.ReadingCombatLog,
                combatLogPath: @"C:\WoW\Logs\WoWCombatLog-current.txt",
                combatLogError: new IOException("Access denied.")
            )
        );

        Assert.Equal("Ready", viewModel.StateTitle);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "WoW is running.",
                "Manual recording is ready, but automatic recording cannot read combat logs: Access denied."
            ),
            viewModel.ReadinessDetail
        );
        Assert.Equal(RecordingStatusHealth.AttentionNeeded, viewModel.StatusHealth);
        Assert.True(viewModel.ManualRecordingCommand.CanExecute(null));
    }

    [Fact]
    public void MissingWowWindowDisablesManualRecordingAndAutomaticReadiness()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                combatLogState: CombatLogReaderState.ReadingCombatLog,
                combatLogPath: @"C:\WoW\Logs\WoWCombatLog-current.txt",
                wowProcessState: WowProcessState.WaitingForProcess
            )
        );

        Assert.Equal("Waiting", viewModel.StateTitle);
        Assert.Equal(RecordingStatusHealth.Waiting, viewModel.StatusHealth);
        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));
    }

    [Fact]
    public void WowProcessWithoutWindowDisablesManualRecording()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                wowProcessState: WowProcessState.WaitingForWindow,
                wowProcessId: 42
            )
        );

        Assert.Equal("Waiting", viewModel.StateTitle);
        Assert.False(viewModel.ManualRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManualCommandDisablesDuringExecutionAndReportsResult()
    {
        var pendingStart = new TaskCompletionSource<RecordingCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            () => pendingStart.Task
        );

        var execution = viewModel.ManualRecordingCommand.ExecuteAsync(null);

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
        string expectedMessage
    )
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            () => Task.FromResult(result)
        );

        await viewModel.ManualRecordingCommand.ExecuteAsync(null);

        Assert.Equal(expectedMessage, viewModel.CommandMessage);
    }

    [Fact]
    public async Task UnexpectedManualCommandFailureIsDisplayed()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            () =>
                Task.FromException<RecordingCommandResult>(
                    new InvalidOperationException("controller unavailable")
                )
        );

        await viewModel.ManualRecordingCommand.ExecuteAsync(null);

        Assert.Equal("Command failed: controller unavailable", viewModel.CommandMessage);
    }

    [Fact]
    public async Task FailedManualCommandUsesFailureDetailsWhenStatusArrives()
    {
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            () => Task.FromResult(RecordingCommandResult.Failed)
        );

        await viewModel.ManualRecordingCommand.ExecuteAsync(null);
        viewModel.ApplyStatus(
            Status(
                RecordingCoordinatorState.Idle,
                lastFailure: new InvalidOperationException("encoder failed")
            )
        );

        Assert.Equal("The recording command failed: encoder failed", viewModel.CommandMessage);
    }

    [Fact]
    public async Task FailedManualCommandUsesOutputDirectoryMessageWhenStatusArrives()
    {
        var failure = new RecordingOutputUnavailableException(
            @"C:\Recordings",
            new IOException("Could not create folder.")
        );
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle),
            () => Task.FromResult(RecordingCommandResult.Failed)
        );

        await viewModel.ManualRecordingCommand.ExecuteAsync(null);
        viewModel.ApplyStatus(Status(RecordingCoordinatorState.Idle, lastFailure: failure));

        Assert.Equal(failure.Message, viewModel.CommandMessage);
        Assert.Equal(failure.Message, viewModel.FailureMessage);
    }

    [Fact]
    public void FailureBannerPersistsUntilDismissedAndReturnsForNewFailure()
    {
        var firstFailure = new InvalidOperationException("capture failed");
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, lastFailure: firstFailure)
        );

        Assert.True(viewModel.IsFailureVisible);
        Assert.Equal("capture failed", viewModel.FailureMessage);

        viewModel.DismissFailureCommand.Execute(null);
        viewModel.ApplyStatus(Status(RecordingCoordinatorState.Idle, lastFailure: firstFailure));

        Assert.False(viewModel.IsFailureVisible);

        viewModel.ApplyStatus(
            Status(
                RecordingCoordinatorState.Idle,
                lastFailure: new InvalidOperationException("finalization failed")
            )
        );

        Assert.True(viewModel.IsFailureVisible);
        Assert.Equal("finalization failed", viewModel.FailureMessage);
        Assert.Equal("Ready", viewModel.StateTitle);
    }

    [Fact]
    public void RecorderFailureUsesNotificationCenterWhenAvailable()
    {
        var failure = new InvalidOperationException("capture failed");
        var notifications = new NotificationCenterViewModel();
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Idle, lastFailure: failure),
            notifications: notifications
        );

        var notification = Assert.Single(notifications.Items);
        Assert.Equal("recorder-failure", notification.Id);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("Recorder needs attention", notification.Title);
        Assert.Equal("capture failed", notification.Message);
        Assert.True(viewModel.IsFailureVisible);

        notification.DismissCommand.Execute(null);
        viewModel.ApplyStatus(Status(RecordingCoordinatorState.Idle, lastFailure: failure));

        Assert.Empty(notifications.Items);
        Assert.False(viewModel.IsFailureVisible);

        viewModel.ApplyStatus(
            Status(
                RecordingCoordinatorState.Idle,
                lastFailure: new InvalidOperationException("finalization failed")
            )
        );

        var newNotification = Assert.Single(notifications.Items);
        Assert.Equal("finalization failed", newNotification.Message);
        Assert.True(viewModel.IsFailureVisible);
    }

    [Fact]
    public void IdleRecorderReportsIdleHealth()
    {
        var viewModel = CreateViewModel(Status(RecordingCoordinatorState.Idle));

        Assert.Equal(RecordingStatusHealth.Idle, viewModel.RecorderHealth);
    }

    [Fact]
    public async Task WowWindowFailureIsShownAsCommandMessageNotRecorderFailure()
    {
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                lastFailure: new InvalidOperationException(
                    "Recorder startup failed.",
                    new CaptureTargetUnavailableException(
                        "Could not find a running World of Warcraft window."
                    )
                )
            ),
            () => Task.FromResult(RecordingCommandResult.Failed)
        );

        await viewModel.ManualRecordingCommand.ExecuteAsync(null);

        Assert.Equal("World of Warcraft is not running.", viewModel.CommandMessage);
        Assert.Equal(RecordingStatusHealth.Idle, viewModel.RecorderHealth);
        Assert.False(viewModel.IsFailureVisible);
        Assert.Null(viewModel.FailureMessage);
    }

    [Fact]
    public async Task ManualCommandStopsWhenRecording()
    {
        var stopCalls = 0;
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Recording,
                new ManualRecordingContext(DateTimeOffset.Now)
            ),
            stopManual: () =>
            {
                stopCalls++;
                return Task.FromResult(RecordingCommandResult.Stopped);
            }
        );

        await viewModel.ManualRecordingCommand.ExecuteAsync(null);

        Assert.Equal(1, stopCalls);
        Assert.Equal("Recording stopped.", viewModel.CommandMessage);
    }

    [Fact]
    public void ListsCatalogRecordingsFromConfiguredDirectory()
    {
        var directory = CreateTempDirectory();

        try
        {
            var older = Path.Combine(directory, "older.mp4");
            var newer = Path.Combine(directory, "newer.mp4");
            var newerStartedAtUtc = new DateTimeOffset(2026, 6, 15, 10, 58, 0, TimeSpan.Zero);
            var recordings = new[]
            {
                CatalogFile(
                    newer,
                    new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
                    startedAtUtc: newerStartedAtUtc
                ),
                CatalogFile(older, new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            };
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>(recordings)
            );
            SelectCategory(viewModel, RecordingListCategory.Manual);

            Assert.Equal("Recording", viewModel.ActivityColumnHeader);
            Assert.Equal("Length", viewModel.DurationColumnHeader);
            Assert.False(viewModel.IsContextColumnVisible);
            Assert.False(viewModel.IsResultColumnVisible);
            Assert.True(viewModel.IsDurationColumnVisible);
            Assert.Collection(
                viewModel.Recordings,
                first =>
                {
                    Assert.Equal(recordings[0].Id, first.Id);
                    Assert.Equal(newer, first.Path);
                    Assert.Equal("newer", first.DisplayName);
                    Assert.Equal(
                        $"{newerStartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                        first.StartedAt
                    );
                    Assert.Equal("newer", first.Activity);
                    Assert.Equal(string.Empty, first.ActivityDetail);
                    Assert.False(first.IsContextVisible);
                    Assert.False(first.IsResultVisible);
                    Assert.True(first.IsDurationVisible);
                    Assert.Equal("-", first.Duration);
                },
                second =>
                {
                    Assert.Equal(older, second.Path);
                    Assert.Equal("older", second.DisplayName);
                }
            );
            Assert.Equal(newer, viewModel.SelectedRecording?.Path);
            Assert.Equal(string.Empty, viewModel.RecordingLibraryStatus);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NewestOverlappingRefreshWinsWithoutDuplicatingRows()
    {
        var directory = CreateTempDirectory();

        try
        {
            var firstRefresh = new TaskCompletionSource<IReadOnlyList<RecordingCatalogFile>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var secondRefresh = new TaskCompletionSource<IReadOnlyList<RecordingCatalogFile>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var older = CatalogFile(
                Path.Combine(directory, "older-result.mp4"),
                new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)
            );
            var newer = CatalogFile(
                Path.Combine(directory, "newer-result.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero)
            );
            var loadCalls = 0;
            var viewModel = CreateViewModel(
                Status(
                    RecordingCoordinatorState.Idle,
                    recordingsDirectory: directory,
                    selectedRecordingCategory: RecordingListCategory.Manual
                ),
                loadRecordings: _ =>
                    ++loadCalls switch
                    {
                        1 => Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([]),
                        2 => firstRefresh.Task,
                        3 => secondRefresh.Task,
                        _ => throw new InvalidOperationException("Unexpected catalog load."),
                    }
            );

            var firstTask = viewModel.RefreshRecordingsAsync();
            var secondTask = viewModel.RefreshRecordingsAsync();
            secondRefresh.SetResult([newer]);
            await secondTask;
            firstRefresh.SetResult([older]);
            await firstTask;

            Assert.Equal(3, loadCalls);
            Assert.Equal(newer.Id, Assert.Single(viewModel.Recordings).Id);
            Assert.Equal(newer.Id, viewModel.SelectedRecording?.Id);
            Assert.Equal(1, CountFor(viewModel, RecordingListCategory.Manual));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ObsoleteRefreshFailureDoesNotReplaceNewerSuccessfulResult()
    {
        var directory = CreateTempDirectory();

        try
        {
            var obsoleteRefresh = new TaskCompletionSource<IReadOnlyList<RecordingCatalogFile>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var currentRefresh = new TaskCompletionSource<IReadOnlyList<RecordingCatalogFile>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var current = CatalogFile(
                Path.Combine(directory, "current.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero)
            );
            var loadCalls = 0;
            var viewModel = CreateViewModel(
                Status(
                    RecordingCoordinatorState.Idle,
                    recordingsDirectory: directory,
                    selectedRecordingCategory: RecordingListCategory.Manual
                ),
                loadRecordings: _ =>
                    ++loadCalls switch
                    {
                        1 => Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([]),
                        2 => obsoleteRefresh.Task,
                        3 => currentRefresh.Task,
                        _ => throw new InvalidOperationException("Unexpected catalog load."),
                    }
            );

            var obsoleteTask = viewModel.RefreshRecordingsAsync();
            var currentTask = viewModel.RefreshRecordingsAsync();
            currentRefresh.SetResult([current]);
            await currentTask;
            obsoleteRefresh.SetException(new IOException("obsolete failure"));
            await obsoleteTask;

            Assert.Equal(current.Id, Assert.Single(viewModel.Recordings).Id);
            Assert.Equal(string.Empty, viewModel.RecordingLibraryStatus);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FiltersRecordingsBySelectedCategoryAndTracksCounts()
    {
        var directory = CreateTempDirectory();

        try
        {
            var challengeMode = CatalogFile(
                Path.Combine(directory, "key.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
                kind: RecordingCatalogKind.ChallengeMode
            );
            var raid = CatalogFile(
                Path.Combine(directory, "boss.mp4"),
                new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
                kind: RecordingCatalogKind.Encounter
            );
            var manual = CatalogFile(
                Path.Combine(directory, "manual.mp4"),
                new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero)
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([
                        challengeMode,
                        raid,
                        manual,
                    ])
            );

            Assert.Equal(1, CountFor(viewModel, RecordingListCategory.ChallengeMode));
            Assert.Equal(1, CountFor(viewModel, RecordingListCategory.RaidEncounter));
            Assert.Equal(1, CountFor(viewModel, RecordingListCategory.Manual));
            Assert.Equal(
                RecordingListCategory.ChallengeMode,
                viewModel.SelectedRecordingCategory.Category
            );
            Assert.Equal(challengeMode.Id, Assert.Single(viewModel.Recordings).Id);

            SelectCategory(viewModel, RecordingListCategory.RaidEncounter);

            Assert.Equal(raid.Id, Assert.Single(viewModel.Recordings).Id);
            Assert.Equal(raid.Id, viewModel.SelectedRecording?.Id);

            SelectCategory(viewModel, RecordingListCategory.Manual);

            Assert.Equal(manual.Id, Assert.Single(viewModel.Recordings).Id);
            Assert.Equal(manual.Id, viewModel.SelectedRecording?.Id);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializesSelectedCategoryFromSettingsAndSavesChanges()
    {
        var savedCategories = new List<RecordingListCategory>();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(
            Status(
                RecordingCoordinatorState.Idle,
                selectedRecordingCategory: RecordingListCategory.Manual
            ),
            saveSelectedRecordingCategory: category =>
            {
                savedCategories.Add(category);
                saved.SetResult();
                return Task.CompletedTask;
            }
        );

        Assert.Equal(RecordingListCategory.Manual, viewModel.SelectedRecordingCategory.Category);

        SelectCategory(viewModel, RecordingListCategory.RaidEncounter);
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal([RecordingListCategory.RaidEncounter], savedCategories);
    }

    [Fact]
    public void ReportsEmptySelectedCategory()
    {
        var directory = CreateTempDirectory();

        try
        {
            var challengeMode = CatalogFile(
                Path.Combine(directory, "key.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
                kind: RecordingCatalogKind.ChallengeMode
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([challengeMode])
            );

            SelectCategory(viewModel, RecordingListCategory.RaidEncounter);

            Assert.Empty(viewModel.Recordings);
            Assert.Null(viewModel.SelectedRecording);
            Assert.Equal("No Raid recordings found yet.", viewModel.RecordingLibraryStatus);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FormatsRaidEncounterColumns()
    {
        var directory = CreateTempDirectory();

        try
        {
            var id = Guid.Parse("F16B49B4-C7B3-4B5E-8E4F-E8843088DE7A");
            var encounterStartedAtUtc = new DateTimeOffset(2026, 6, 17, 20, 28, 32, TimeSpan.Zero);
            var recording = CatalogFile(
                Path.Combine(directory, "rotmire.mp4"),
                new DateTimeOffset(2026, 6, 17, 20, 36, 20, TimeSpan.Zero),
                id: id,
                kind: RecordingCatalogKind.Encounter,
                raidEncounter: new RaidEncounterEntry(
                    id,
                    new DateTimeOffset(2026, 6, 17, 20, 28, 32, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 17, 20, 36, 19, TimeSpan.Zero),
                    3159,
                    "Rotmire",
                    WowDifficultyIds.FlexibleMythicRaid,
                    20,
                    1592,
                    encounterStartedAtUtc,
                    RaidEncounterOutcome.Kill,
                    new DateTimeOffset(2026, 6, 17, 20, 36, 19, TimeSpan.Zero),
                    466563,
                    4
                )
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([recording])
            );
            SelectCategory(viewModel, RecordingListCategory.RaidEncounter);

            var item = Assert.Single(viewModel.Recordings);

            Assert.Equal("Boss", viewModel.ActivityColumnHeader);
            Assert.Equal("Pull #", viewModel.PullNumberColumnHeader);
            Assert.Equal("Difficulty", viewModel.ContextColumnHeader);
            Assert.Equal("Result", viewModel.ResultColumnHeader);
            Assert.Equal("Pull Time", viewModel.DurationColumnHeader);
            Assert.True(viewModel.IsPullNumberColumnVisible);
            Assert.True(viewModel.IsContextColumnVisible);
            Assert.True(viewModel.IsResultColumnVisible);
            Assert.True(viewModel.IsDurationColumnVisible);
            Assert.Equal("rotmire", item.DisplayName);
            Assert.Equal($"{encounterStartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", item.StartedAt);
            Assert.Equal("4", item.PullNumber);
            Assert.Equal("Rotmire", item.Activity);
            Assert.Equal(string.Empty, item.ActivityDetail);
            Assert.True(item.IsPullNumberVisible);
            Assert.True(item.IsContextVisible);
            Assert.True(item.IsResultVisible);
            Assert.True(item.IsDurationVisible);
            Assert.Equal("Mythic", item.Context);
            Assert.Equal("Kill", item.Result);
            Assert.Equal("07:46", item.Duration);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FormatsChallengeModeColumns()
    {
        var directory = CreateTempDirectory();

        try
        {
            var id = Guid.Parse("7DA1220D-1E5D-4737-8E74-8998F9D99AC1");
            var challengeStartedAtUtc = new DateTimeOffset(2026, 6, 14, 20, 37, 55, TimeSpan.Zero);
            var recording = CatalogFile(
                Path.Combine(directory, "magisters-terrace.mp4"),
                new DateTimeOffset(2026, 6, 14, 21, 8, 45, TimeSpan.Zero),
                id: id,
                kind: RecordingCatalogKind.ChallengeMode,
                challengeMode: new ChallengeModeEntry(
                    id,
                    challengeStartedAtUtc,
                    new DateTimeOffset(2026, 6, 14, 21, 8, 45, TimeSpan.Zero),
                    "Magisters' Terrace",
                    2811,
                    558,
                    22,
                    [9, 10, 147],
                    challengeStartedAtUtc,
                    ChallengeModeOutcome.Timed,
                    new DateTimeOffset(2026, 6, 14, 21, 8, 45, TimeSpan.Zero),
                    1850000,
                    32.5,
                    1800
                ),
                startedAtUtc: new DateTimeOffset(2026, 6, 14, 20, 37, 45, TimeSpan.Zero),
                endedAtUtc: new DateTimeOffset(2026, 6, 14, 21, 9, 5, TimeSpan.Zero)
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([recording])
            );

            var item = Assert.Single(viewModel.Recordings);

            Assert.Equal("Dungeon", viewModel.ActivityColumnHeader);
            Assert.Equal("Key", viewModel.ContextColumnHeader);
            Assert.Equal("Result", viewModel.ResultColumnHeader);
            Assert.Equal("Length", viewModel.DurationColumnHeader);
            Assert.True(viewModel.IsDurationColumnVisible);
            Assert.True(viewModel.IsContextColumnVisible);
            Assert.True(viewModel.IsResultColumnVisible);
            Assert.Equal("magisters-terrace", item.DisplayName);
            Assert.Equal($"{challengeStartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", item.StartedAt);
            Assert.Equal("Magisters' Terrace", item.Activity);
            Assert.Equal("Affix IDs 9, 10, 147", item.ActivityDetail);
            Assert.DoesNotContain("Affix IDs", item.ToolTip);
            Assert.True(item.IsContextVisible);
            Assert.True(item.IsResultVisible);
            Assert.True(item.IsDurationVisible);
            Assert.Equal("+22", item.Context);
            Assert.Equal("Timed", item.Result);
            Assert.Equal("31:20", item.Duration);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeleteSelectedRecordingConfirmsDeletesAndRemovesVisibleItem()
    {
        var directory = CreateTempDirectory();

        try
        {
            var loadCalls = 0;
            var deletedIds = new List<Guid>();
            var deleteStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var deleteCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var firstId = Guid.Parse("5290C068-36C1-47BB-8B8C-60A6AE506695");
            var secondId = Guid.Parse("4443274B-22F2-471C-8676-46F63F8A7B87");
            var first = CatalogFile(
                Path.Combine(directory, "first.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
                id: firstId
            );
            var second = CatalogFile(
                Path.Combine(directory, "second.mp4"),
                new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
                id: secondId
            );
            var recordings = new List<RecordingCatalogFile> { first, second };
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                {
                    loadCalls++;
                    return Task.FromResult<IReadOnlyList<RecordingCatalogFile>>(
                        recordings.ToList()
                    );
                },
                deleteRecording: id =>
                {
                    deletedIds.Add(id);
                    recordings.RemoveAll(recording => recording.Id == id);
                    deleteStarted.SetResult();
                    return deleteCompletion.Task;
                },
                confirmPermanentDelete: _ => true
            );
            SelectCategory(viewModel, RecordingListCategory.Manual);

            var deleteTask = viewModel.DeleteSelectedRecordingCommand.ExecuteAsync(null);
            await deleteStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(1, loadCalls);
            Assert.Equal([firstId], deletedIds);
            var remaining = Assert.Single(viewModel.Recordings);
            Assert.Equal(secondId, remaining.Id);
            Assert.Equal(secondId, viewModel.SelectedRecording?.Id);
            Assert.False(deleteTask.IsCompleted);

            deleteCompletion.SetResult();
            await deleteTask;

            Assert.Equal("Recording deleted.", viewModel.CommandMessage);
            Assert.True(viewModel.IsCommandMessageVisible);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RefreshStartedBeforeDeleteCannotRestoreDeletedRecording()
    {
        var directory = CreateTempDirectory();

        try
        {
            var deleted = CatalogFile(
                Path.Combine(directory, "deleted.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero)
            );
            var kept = CatalogFile(
                Path.Combine(directory, "kept.mp4"),
                new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)
            );
            var staleRefresh = new TaskCompletionSource<IReadOnlyList<RecordingCatalogFile>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var deleteStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var deleteCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var loadCalls = 0;
            var viewModel = CreateViewModel(
                Status(
                    RecordingCoordinatorState.Idle,
                    recordingsDirectory: directory,
                    selectedRecordingCategory: RecordingListCategory.Manual
                ),
                loadRecordings: _ =>
                    ++loadCalls switch
                    {
                        1 => Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([deleted, kept]),
                        2 => staleRefresh.Task,
                        _ => throw new InvalidOperationException("Unexpected catalog load."),
                    },
                deleteRecording: _ =>
                {
                    deleteStarted.SetResult();
                    return deleteCompletion.Task;
                },
                confirmPermanentDelete: _ => true
            );

            var refreshTask = viewModel.RefreshRecordingsAsync();
            var deleteTask = viewModel.DeleteSelectedRecordingCommand.ExecuteAsync(null);
            await deleteStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken
            );
            staleRefresh.SetResult([deleted, kept]);
            await refreshTask;

            Assert.Equal(kept.Id, Assert.Single(viewModel.Recordings).Id);
            deleteCompletion.SetResult();
            await deleteTask;
            Assert.Equal(kept.Id, Assert.Single(viewModel.Recordings).Id);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeleteSelectedRecordingDoesNothingWhenConfirmationIsDeclined()
    {
        var directory = CreateTempDirectory();

        try
        {
            var deleteCalls = 0;
            var recording = CatalogFile(
                Path.Combine(directory, "kept.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero)
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([recording]),
                deleteRecording: _ =>
                {
                    deleteCalls++;
                    return Task.CompletedTask;
                },
                confirmPermanentDelete: _ => false
            );
            SelectCategory(viewModel, RecordingListCategory.Manual);

            await viewModel.DeleteSelectedRecordingCommand.ExecuteAsync(null);

            Assert.Equal(0, deleteCalls);
            Assert.Equal(recording.Id, viewModel.SelectedRecording?.Id);
            Assert.Null(viewModel.CommandMessage);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeleteSelectedRecordingFailureRestoresItemWithoutStealingSelection()
    {
        var directory = CreateTempDirectory();

        try
        {
            var deletedId = Guid.Parse("A818D7C1-A424-4F3D-A3F4-AC6D1E08D0D8");
            var keptId = Guid.Parse("6A41D8E0-FA61-4AE8-BED6-2D36D0221494");
            var deleted = CatalogFile(
                Path.Combine(directory, "locked.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
                id: deletedId
            );
            var kept = CatalogFile(
                Path.Combine(directory, "kept.mp4"),
                new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
                id: keptId
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([deleted, kept]),
                deleteRecording: _ => Task.FromException(new IOException("file is locked")),
                confirmPermanentDelete: _ => true
            );
            SelectCategory(viewModel, RecordingListCategory.Manual);

            await viewModel.DeleteSelectedRecordingCommand.ExecuteAsync(null);

            Assert.Collection(
                viewModel.Recordings,
                first => Assert.Equal(deletedId, first.Id),
                second => Assert.Equal(keptId, second.Id)
            );
            Assert.Equal(keptId, viewModel.SelectedRecording?.Id);
            Assert.Equal("Could not delete recording: file is locked", viewModel.CommandMessage);
            Assert.True(viewModel.IsCommandMessageVisible);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeleteSelectedRecordingFailureAfterCategoryChangeDoesNotSelectHiddenRecording()
    {
        var directory = CreateTempDirectory();

        try
        {
            var deleteStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var deleteCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var manualId = Guid.Parse("F3750848-10E7-4191-8F70-69E52E09C183");
            var raidId = Guid.Parse("352D60D6-7C74-45C9-9E23-C4F6BA5E390B");
            var manual = CatalogFile(
                Path.Combine(directory, "manual.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
                id: manualId
            );
            var raid = CatalogFile(
                Path.Combine(directory, "raid.mp4"),
                new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
                id: raidId,
                kind: RecordingCatalogKind.Encounter
            );
            var viewModel = CreateViewModel(
                Status(
                    RecordingCoordinatorState.Idle,
                    recordingsDirectory: directory,
                    selectedRecordingCategory: RecordingListCategory.Manual
                ),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([manual, raid]),
                deleteRecording: _ =>
                {
                    deleteStarted.SetResult();
                    return deleteCompletion.Task;
                },
                confirmPermanentDelete: _ => true
            );

            var deleteTask = viewModel.DeleteSelectedRecordingCommand.ExecuteAsync(null);
            await deleteStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken
            );

            SelectCategory(viewModel, RecordingListCategory.RaidEncounter);

            deleteCompletion.SetException(new IOException("file is locked"));
            await deleteTask;

            Assert.Equal(1, CountFor(viewModel, RecordingListCategory.Manual));
            Assert.Equal(raidId, Assert.Single(viewModel.Recordings).Id);
            Assert.Equal(raidId, viewModel.SelectedRecording?.Id);
            Assert.Equal("Could not delete recording: file is locked", viewModel.CommandMessage);
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
        Assert.False(viewModel.DeleteSelectedRecordingCommand.CanExecute(null));
        Assert.Equal(
            "Choose a recordings directory in settings to review videos here.",
            viewModel.RecordingLibraryStatus
        );

        SelectCategory(viewModel, RecordingListCategory.Manual);

        Assert.Empty(viewModel.Recordings);
        Assert.Null(viewModel.SelectedRecording);
        Assert.Equal(
            "Choose a recordings directory in settings to review videos here.",
            viewModel.RecordingLibraryStatus
        );
    }

    [Fact]
    public void SavedCountStatusChangeSelectsCompletedRecording()
    {
        var directory = CreateTempDirectory();

        try
        {
            var older = WriteRecording(
                directory,
                "older.mp4",
                "older",
                new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc)
            );
            WriteRecording(
                directory,
                "newer.mp4",
                "newer",
                new DateTime(2026, 6, 15, 11, 0, 0, DateTimeKind.Utc)
            );
            var recordings = new List<RecordingCatalogFile>
            {
                CatalogFile(
                    Path.Combine(directory, "newer.mp4"),
                    new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero)
                ),
                CatalogFile(older, new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            };
            var completedPath = Path.Combine(directory, "completed.mp4");
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>(recordings)
            );
            SelectCategory(viewModel, RecordingListCategory.Manual);
            viewModel.SelectedRecording = viewModel.Recordings.Single(recording =>
                recording.Path == older
            );
            viewModel.ApplyStatus(
                Status(
                    RecordingCoordinatorState.Recording,
                    new ManualRecordingContext(DateTimeOffset.Now),
                    recordingsDirectory: directory,
                    activeOutputPath: completedPath
                )
            );

            WriteRecording(
                directory,
                "completed.mp4",
                "completed",
                new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc)
            );
            recordings.Insert(
                1,
                CatalogFile(
                    completedPath,
                    new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero)
                )
            );
            viewModel.ApplyStatus(
                Status(
                    RecordingCoordinatorState.Idle,
                    recordingsDirectory: directory,
                    savedCount: 1
                )
            );

            Assert.Equal(completedPath, viewModel.SelectedRecording?.Path);
            Assert.Equal("newer", viewModel.Recordings[0].DisplayName);
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
        var viewModel = CreateViewModel(
            Status(RecordingCoordinatorState.Recording, new ManualRecordingContext(startedAt))
        );

        viewModel.UpdateDuration(startedAt.AddSeconds(elapsedSeconds));

        Assert.Equal(expected, viewModel.Duration);
    }

    private static void SelectCategory(
        RecordingsViewModel viewModel,
        RecordingListCategory category
    )
    {
        viewModel.SelectedRecordingCategory = viewModel.RecordingCategories.Single(tab =>
            tab.Category == category
        );
    }

    private static int CountFor(RecordingsViewModel viewModel, RecordingListCategory category)
    {
        return viewModel.RecordingCategories.Single(tab => tab.Category == category).Count;
    }

    private static RecordingsViewModel CreateViewModel(
        ApplicationStatus status,
        Func<Task<RecordingCommandResult>>? startManual = null,
        Func<Task<RecordingCommandResult>>? stopManual = null,
        Func<string, Task<IReadOnlyList<RecordingCatalogFile>>>? loadRecordings = null,
        Func<Guid, Task>? deleteRecording = null,
        Func<RecordingListItem, bool>? confirmPermanentDelete = null,
        Func<RecordingListCategory, Task>? saveSelectedRecordingCategory = null,
        Func<Task>? testVideoEncoding = null,
        NotificationCenterViewModel? notifications = null,
        Func<int, bool, Task<bool>>? savePlaybackAudioState = null
    )
    {
        return new RecordingsViewModel(
            status,
            startManual ?? (() => Task.FromResult(RecordingCommandResult.Started)),
            stopManual ?? (() => Task.FromResult(RecordingCommandResult.Stopped)),
            testVideoEncoding ?? (() => Task.CompletedTask),
            loadRecordings ?? (_ => Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([])),
            deleteRecording ?? (_ => Task.CompletedTask),
            confirmPermanentDelete ?? (_ => true),
            () => Task.CompletedTask,
            saveSelectedRecordingCategory ?? (_ => Task.CompletedTask),
            notifications,
            savePlaybackAudioState
        );
    }

    private static ApplicationStatus Status(
        RecordingCoordinatorState state,
        RecordingContext? context = null,
        Exception? lastFailure = null,
        string? recordingsDirectory = null,
        int savedCount = 0,
        CombatLogReaderState combatLogState = CombatLogReaderState.WaitingForCombatLog,
        string? combatLogPath = null,
        Exception? combatLogError = null,
        WowProcessState wowProcessState = WowProcessState.WindowAvailable,
        int? wowProcessId = 10,
        string? wowWindowTitle = "World of Warcraft",
        string? activeOutputPath = null,
        RecordingListCategory selectedRecordingCategory = RecordingListCategory.ChallengeMode,
        bool includeSelectedVideoProfile = true,
        bool includeEncoderCalibrationResults = true,
        int encoderCalibrationVersion = EncoderCalibrationSettings.CurrentVersion,
        int playbackVolumePercent = UiSettings.DefaultPlaybackVolumePercent,
        bool isPlaybackMuted = false
    )
    {
        var selectedProfile = includeSelectedVideoProfile
            ? new VideoProfileSelection
            {
                Codec = VideoCodec.H265,
                Provider = VideoEncoderProvider.NvidiaNvenc,
            }
            : null;
        RecordingOwner? owner = context switch
        {
            ManualRecordingContext => RecordingOwner.Manual,
            ChallengeRecordingContext => RecordingOwner.ChallengeMode,
            EncounterRecordingContext => RecordingOwner.Encounter,
            _ => null,
        };
        var settings = new PullWatchSettings
        {
            RecordingsDirectory = recordingsDirectory,
            Video = new VideoSettings { SelectedProfile = selectedProfile },
            EncoderCalibration = new EncoderCalibrationSettings
            {
                Version = encoderCalibrationVersion,
                FfmpegPath = CurrentEncoderEnvironment.FfmpegPath,
                FfmpegVersion = CurrentEncoderEnvironment.FfmpegVersion,
                FfmpegSha256 = CurrentEncoderEnvironment.FfmpegSha256,
                Results =
                    includeEncoderCalibrationResults && selectedProfile is not null
                        ? [CalibrationResult(selectedProfile)]
                        : [],
            },
            Ui = new UiSettings
            {
                SelectedRecordingCategory = selectedRecordingCategory,
                PlaybackVolumePercent = playbackVolumePercent,
                IsPlaybackMuted = isPlaybackMuted,
            },
        };

        return new ApplicationStatus(
            settings,
            new RecordingCoordinatorStatus(
                state,
                owner,
                null,
                context,
                null,
                null,
                lastFailure,
                state == RecordingCoordinatorState.Idle
                    ? null
                    : activeOutputPath ?? @"C:\Recordings\active.mp4"
            )
            {
                Statistics = new RecordingStatistics(0, savedCount),
            },
            new CombatLogReaderStatus(combatLogState, combatLogPath, null, combatLogError),
            new WowProcessStatus(wowProcessState, wowProcessId, null, wowWindowTitle, null),
            EncoderCalibrationStatusEvaluator.Evaluate(settings, CurrentEncoderEnvironment)
        );
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PullWatch.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static RecordingCatalogFile CatalogFile(
        string path,
        DateTimeOffset modifiedAtUtc,
        long sizeBytes = 1024,
        Guid? id = null,
        RecordingCatalogKind kind = RecordingCatalogKind.Manual,
        RaidEncounterEntry? raidEncounter = null,
        ChallengeModeEntry? challengeMode = null,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? endedAtUtc = null
    )
    {
        return new RecordingCatalogFile(
            id ?? Guid.NewGuid(),
            path,
            kind,
            startedAtUtc,
            endedAtUtc,
            sizeBytes,
            modifiedAtUtc,
            raidEncounter,
            challengeMode
        );
    }

    private static string WriteRecording(
        string directory,
        string fileName,
        string content,
        DateTime lastWriteTimeUtc
    )
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        return path;
    }

    private static EncoderCalibrationResult CalibrationResult(VideoProfileSelection profile)
    {
        return new EncoderCalibrationResult
        {
            Codec = profile.Codec,
            Provider = profile.Provider,
            EncoderName = profile.Codec == VideoCodec.H265 ? "hevc_nvenc" : "h264_nvenc",
            Passed = true,
            Message = "Available",
            Width = 1920,
            Height = 1080,
            DurationSeconds = 2.0,
        };
    }
}
