namespace PullWatch.Tests;

public sealed class RecordingsViewModelTests
{
    [Theory]
    [InlineData(RecordingCoordinatorState.Idle, "Ready", true, false, "Manual start")]
    [InlineData(RecordingCoordinatorState.Starting, "Processing", false, false, "Manual start")]
    [InlineData(RecordingCoordinatorState.Recording, "Recording", true, true, "Manual stop")]
    [InlineData(RecordingCoordinatorState.Stopping, "Processing", false, false, "Manual start")]
    public void AppliesEveryRecordingState(
        RecordingCoordinatorState state,
        string expectedTitle,
        bool canRunManualCommand,
        bool isManualStopMode,
        string expectedManualButtonText
    )
    {
        var viewModel = CreateViewModel(Status(state));

        Assert.Equal(expectedTitle, viewModel.StateTitle);
        Assert.Equal(canRunManualCommand, viewModel.ManualRecordingCommand.CanExecute(null));
        Assert.Equal(isManualStopMode, viewModel.IsManualStopMode);
        Assert.Equal(expectedManualButtonText, viewModel.ManualRecordingButtonText);
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
                    Assert.Equal("Manual recording", first.EncounterName);
                    Assert.Equal("-", first.Difficulty);
                    Assert.Equal("-", first.Outcome);
                    Assert.Equal("-", first.FightDuration);
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
                    466563
                )
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([recording])
            );

            var item = Assert.Single(viewModel.Recordings);

            Assert.Equal("rotmire", item.DisplayName);
            Assert.Equal($"{encounterStartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", item.StartedAt);
            Assert.Equal("Rotmire", item.EncounterName);
            Assert.Equal("Mythic", item.Difficulty);
            Assert.Equal("Kill", item.Outcome);
            Assert.Equal("07:46", item.FightDuration);
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
                )
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([recording])
            );

            var item = Assert.Single(viewModel.Recordings);

            Assert.Equal("magisters-terrace", item.DisplayName);
            Assert.Equal($"{challengeStartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}", item.StartedAt);
            Assert.Equal("Magisters' Terrace", item.EncounterName);
            Assert.Equal("+22", item.Difficulty);
            Assert.Equal("Timed", item.Outcome);
            Assert.Equal("30:50", item.FightDuration);
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
    public async Task DeleteSelectedRecordingFailureKeepsVisibleSelectionAndReportsError()
    {
        var directory = CreateTempDirectory();

        try
        {
            var recording = CatalogFile(
                Path.Combine(directory, "locked.mp4"),
                new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero)
            );
            var viewModel = CreateViewModel(
                Status(RecordingCoordinatorState.Idle, recordingsDirectory: directory),
                loadRecordings: _ =>
                    Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([recording]),
                deleteRecording: _ => Task.FromException(new IOException("file is locked")),
                confirmPermanentDelete: _ => true
            );

            await viewModel.DeleteSelectedRecordingCommand.ExecuteAsync(null);

            var visibleRecording = Assert.Single(viewModel.Recordings);
            Assert.Equal(recording.Id, visibleRecording.Id);
            Assert.Equal(recording.Id, viewModel.SelectedRecording?.Id);
            Assert.Equal("Could not delete recording: file is locked", viewModel.CommandMessage);
            Assert.True(viewModel.IsCommandMessageVisible);
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

    private static RecordingsViewModel CreateViewModel(
        ApplicationStatus status,
        Func<Task<RecordingCommandResult>>? startManual = null,
        Func<Task<RecordingCommandResult>>? stopManual = null,
        Func<string, Task<IReadOnlyList<RecordingCatalogFile>>>? loadRecordings = null,
        Func<Guid, Task>? deleteRecording = null,
        Func<RecordingListItem, bool>? confirmPermanentDelete = null
    )
    {
        return new RecordingsViewModel(
            status,
            startManual ?? (() => Task.FromResult(RecordingCommandResult.Started)),
            stopManual ?? (() => Task.FromResult(RecordingCommandResult.Stopped)),
            loadRecordings ?? (_ => Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([])),
            deleteRecording ?? (_ => Task.CompletedTask),
            confirmPermanentDelete ?? (_ => true),
            () => Task.CompletedTask
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
        string? activeOutputPath = null
    )
    {
        RecordingOwner? owner = context switch
        {
            ManualRecordingContext => RecordingOwner.Manual,
            ChallengeRecordingContext => RecordingOwner.ChallengeMode,
            EncounterRecordingContext => RecordingOwner.Encounter,
            _ => null,
        };

        return new ApplicationStatus(
            new PullWatchSettings { RecordingsDirectory = recordingsDirectory },
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
            new WowProcessStatus(wowProcessState, wowProcessId, wowWindowTitle, null)
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
        DateTimeOffset? startedAtUtc = null
    )
    {
        return new RecordingCatalogFile(
            id ?? Guid.NewGuid(),
            path,
            kind,
            startedAtUtc,
            null,
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
}
