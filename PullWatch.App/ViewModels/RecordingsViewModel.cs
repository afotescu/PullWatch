using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace PullWatch;

public sealed partial class RecordingsViewModel : ObservableObject
{
    private const string TargetUnavailableMessage = "World of Warcraft is not running.";
    private const string NoRecordingsDirectoryMessage =
        "Choose a recordings directory in settings to review videos here.";
    private const string NoRecordingsMessage = "No finished .mp4 recordings found yet.";
    private const string MissingMetadataValue = "-";
    private const int NormalRaidDifficultyId = 14;
    private const int HeroicRaidDifficultyId = 15;
    private const int MythicRaidDifficultyId = 16;
    private const int RaidFinderDifficultyId = 17;
    private const int FlexibleMythicRaidDifficultyId = 233;
    private const double ContextColumnWidthValue = 92;
    private const double ResultColumnWidthValue = 92;
    private const double DurationColumnWidthValue = 104;

    private readonly Func<Task<RecordingCommandResult>> _startManual;
    private readonly Func<Task<RecordingCommandResult>> _stopManual;
    private readonly Func<string, Task<IReadOnlyList<RecordingCatalogFile>>> _loadRecordings;
    private readonly Func<Guid, Task> _deleteRecording;
    private readonly Func<RecordingListItem, bool> _confirmPermanentDelete;
    private readonly Func<Task> _openRecordingsFolder;
    private readonly Func<RecordingListCategory, Task> _saveSelectedRecordingCategory;
    private readonly List<RecordingListItem> _allRecordings = new();
    private RecordingCoordinatorStatus _recording;
    private CombatLogReaderStatus _combatLog;
    private WowProcessStatus _wowProcess;
    private string? _recordingsDirectory;
    private Exception? _dismissedFailure;
    private string _duration = "00:00:00";
    private string _recordingLibraryStatus = NoRecordingsDirectoryMessage;
    private string? _commandMessage;
    private RecordingListItem? _selectedRecording;
    private RecordingCategoryTab _selectedRecordingCategory = null!;
    private int _knownSavedCount;

    public RecordingsViewModel(
        ApplicationStatus initialStatus,
        Func<Task<RecordingCommandResult>> startManual,
        Func<Task<RecordingCommandResult>> stopManual,
        Func<string, Task<IReadOnlyList<RecordingCatalogFile>>> loadRecordings,
        Func<Guid, Task> deleteRecording,
        Func<RecordingListItem, bool> confirmPermanentDelete,
        Func<Task> openRecordingsFolder,
        Func<RecordingListCategory, Task> saveSelectedRecordingCategory
    )
    {
        _recording = initialStatus.Recording;
        _combatLog = initialStatus.CombatLog;
        _wowProcess = initialStatus.WowProcess;
        _recordingsDirectory = initialStatus.EffectiveSettings?.RecordingsDirectory;
        _knownSavedCount = initialStatus.Recording.Statistics.SavedCount;
        _startManual = startManual;
        _stopManual = stopManual;
        _loadRecordings = loadRecordings;
        _deleteRecording = deleteRecording;
        _confirmPermanentDelete = confirmPermanentDelete;
        _openRecordingsFolder = openRecordingsFolder;
        _saveSelectedRecordingCategory = saveSelectedRecordingCategory;
        RecordingCategories =
        [
            new RecordingCategoryTab(RecordingListCategory.ChallengeMode, "Mythic+"),
            new RecordingCategoryTab(RecordingListCategory.RaidEncounter, "Raid"),
            new RecordingCategoryTab(RecordingListCategory.Manual, "Manual"),
        ];
        _selectedRecordingCategory =
            GetRecordingCategoryTab(
                initialStatus.EffectiveSettings?.Ui.SelectedRecordingCategory
                    ?? RecordingListCategory.ChallengeMode
            ) ?? RecordingCategories[0];
        ApplyStatus(initialStatus);
        _ = RefreshRecordingsAsync();
    }

    public ObservableCollection<RecordingCategoryTab> RecordingCategories { get; }

    public ObservableCollection<RecordingListItem> Recordings { get; } = new();

    public string ActivityColumnHeader => CurrentColumnHeaders.Activity;

    public string ContextColumnHeader => CurrentColumnHeaders.Context;

    public string ResultColumnHeader => CurrentColumnHeaders.Result;

    public string DurationColumnHeader => CurrentColumnHeaders.Duration;

    public bool IsContextColumnVisible => CurrentColumnHeaders.IsContextVisible;

    public bool IsResultColumnVisible => CurrentColumnHeaders.IsResultVisible;

    public bool IsDurationColumnVisible => CurrentColumnHeaders.IsDurationVisible;

    public GridLength ContextColumnWidth =>
        IsContextColumnVisible ? new GridLength(ContextColumnWidthValue) : new GridLength(0);

    public GridLength ResultColumnWidth =>
        IsResultColumnVisible ? new GridLength(ResultColumnWidthValue) : new GridLength(0);

    public GridLength DurationColumnWidth =>
        IsDurationColumnVisible ? new GridLength(DurationColumnWidthValue) : new GridLength(0);

    public GridLength ScrollBarColumnWidth => new(SystemParameters.VerticalScrollBarWidth);

    private RecordingListColumnHeaders CurrentColumnHeaders =>
        GetColumnHeaders(SelectedRecordingCategory.Category);

    public RecordingCategoryTab SelectedRecordingCategory
    {
        get => _selectedRecordingCategory;
        set
        {
            if (value is not null && SetProperty(ref _selectedRecordingCategory, value))
            {
                NotifyRecordingColumnHeadersChanged();
                ApplyRecordingFilter();
                _ = SaveSelectedRecordingCategoryAsync(value.Category);
            }
        }
    }

    public string StateTitle => GetStateTitle(_recording, _wowProcess);

    public string ReadinessDetail => GetReadinessDetail(_recording, _combatLog, _wowProcess);

    public RecordingStatusHealth StatusHealth =>
        GetStatusHealth(_recording, _combatLog, _wowProcess);

    public bool IsRecordingActive => _recording.State == RecordingCoordinatorState.Recording;

    public bool IsRecordingPulseActive =>
        IsRecordingActive && StatusHealth == RecordingStatusHealth.Active;

    public string Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    public RecordingListItem? SelectedRecording
    {
        get => _selectedRecording;
        set
        {
            if (SetProperty(ref _selectedRecording, value))
            {
                OnPropertyChanged(nameof(IsPlayerPlaceholderVisible));
                DeleteSelectedRecordingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RecordingLibraryStatus
    {
        get => _recordingLibraryStatus;
        private set => SetProperty(ref _recordingLibraryStatus, value);
    }

    public bool IsPlayerPlaceholderVisible => SelectedRecording is null;

    public RecordingStatusHealth RecorderHealth =>
        _recording.LastFailure is not null && !IsTargetUnavailableFailure(_recording.LastFailure)
            ? RecordingStatusHealth.AttentionNeeded
        : _recording.State == RecordingCoordinatorState.Idle ? RecordingStatusHealth.Idle
        : RecordingStatusHealth.Active;

    public string? FailureMessage =>
        IsTargetUnavailableFailure(_recording.LastFailure) ? null : _recording.LastFailure?.Message;

    public string? CommandMessage
    {
        get => _commandMessage;
        private set
        {
            if (SetProperty(ref _commandMessage, value))
            {
                OnPropertyChanged(nameof(IsCommandMessageVisible));
            }
        }
    }

    public bool IsCommandMessageVisible => !string.IsNullOrWhiteSpace(CommandMessage);

    public bool IsFailureVisible =>
        _recording.LastFailure is not null
        && !IsTargetUnavailableFailure(_recording.LastFailure)
        && !ReferenceEquals(_recording.LastFailure, _dismissedFailure);

    public bool IsManualStopMode => _recording.State == RecordingCoordinatorState.Recording;

    public string ManualRecordingButtonText => IsManualStopMode ? "Manual stop" : "Manual start";

    public string CollapsedStatusToolTip =>
        _recording.State switch
        {
            RecordingCoordinatorState.Recording => "Recording. Click to stop.",
            RecordingCoordinatorState.Idle when _wowProcess.IsWindowAvailable =>
                "Idle. Click to start manual recording.",
            _ => ReadinessDetail,
        };

    public void ApplyStatus(ApplicationStatus status)
    {
        var previousDirectory = _recordingsDirectory;
        var previousSavedCount = _knownSavedCount;
        var previousActiveOutputPath = _recording.ActiveOutputPath;
        _recording = status.Recording;
        _combatLog = status.CombatLog;
        _wowProcess = status.WowProcess;
        _recordingsDirectory = status.EffectiveSettings?.RecordingsDirectory;
        _knownSavedCount = status.Recording.Statistics.SavedCount;

        if (CommandMessage == "The recording command failed." && _recording.LastFailure is not null)
        {
            CommandMessage = GetFailureCommandMessage(_recording.LastFailure);
        }

        UpdateDuration();
        if (
            !PathsEqual(previousDirectory, _recordingsDirectory)
            || previousSavedCount != _knownSavedCount
        )
        {
            var savedRecordingCompleted = _knownSavedCount > previousSavedCount;
            _ = RefreshRecordingsAsync(
                savedRecordingCompleted ? previousActiveOutputPath : null,
                savedRecordingCompleted
            );
        }

        OnPropertyChanged(string.Empty);
        ManualRecordingCommand.NotifyCanExecuteChanged();
        DeleteSelectedRecordingCommand.NotifyCanExecuteChanged();
        DismissFailureCommand.NotifyCanExecuteChanged();
    }

    public void UpdateDuration(DateTimeOffset? now = null)
    {
        Duration =
            _recording.Context is null || _recording.State == RecordingCoordinatorState.Idle
                ? "00:00:00"
                : RecordingTimeFormatter.FormatRecordingDuration(
                    (now ?? DateTimeOffset.Now) - _recording.Context.StartedAt
                );
    }

    private bool CanRunManualCommand =>
        _recording.State == RecordingCoordinatorState.Recording
        || (_recording.State == RecordingCoordinatorState.Idle && _wowProcess.IsWindowAvailable);

    private bool CanDeleteSelectedRecording => SelectedRecording is not null;

    internal async Task RefreshRecordingsAsync(
        string? preferredSelectionPath = null,
        bool preferMostRecent = false
    )
    {
        var existingSelectionPath = SelectedRecording?.Path;
        _allRecordings.Clear();
        Recordings.Clear();
        UpdateCategoryCounts();

        if (string.IsNullOrWhiteSpace(_recordingsDirectory))
        {
            SelectedRecording = null;
            RecordingLibraryStatus = NoRecordingsDirectoryMessage;
            return;
        }

        try
        {
            var recordings = await _loadRecordings(_recordingsDirectory);

            foreach (var recording in CreateRecordingListItems(recordings))
            {
                _allRecordings.Add(recording);
            }

            UpdateCategoryCounts();

            var preferredRecording = FindAllRecordingByPath(preferredSelectionPath);
            if (
                preferredRecording is not null
                && preferredRecording.Category != SelectedRecordingCategory.Category
            )
            {
                SelectRecordingCategory(preferredRecording.Category);
            }

            ApplyRecordingFilter(
                preferredRecording?.Path,
                preferMostRecent,
                preferMostRecent ? null : existingSelectionPath
            );
        }
        catch (Exception exception)
        {
            SelectedRecording = null;
            RecordingLibraryStatus = $"Could not read recordings catalog: {exception.Message}";
        }
    }

    internal static IReadOnlyList<RecordingListItem> CreateRecordingListItems(
        IReadOnlyList<RecordingCatalogFile> recordings
    )
    {
        return recordings
            .Select(file =>
            {
                var displayName = Path.GetFileNameWithoutExtension(file.FilePath);

                return new RecordingListItem(
                    file.Id,
                    GetRecordingCategory(file),
                    file.FilePath,
                    displayName,
                    FormatStartedAt(file),
                    GetActivity(file, displayName),
                    GetActivityDetail(file),
                    FormatContext(file),
                    FormatResult(file),
                    FormatActivityDuration(file),
                    file.ModifiedAtUtc.ToLocalTime(),
                    file.SizeBytes
                );
            })
            .ToList();
    }

    private void ApplyRecordingFilter(
        string? preferredSelectionPath = null,
        bool preferMostRecent = false,
        string? existingSelectionPath = null
    )
    {
        existingSelectionPath ??= SelectedRecording?.Path;
        Recordings.Clear();

        if (string.IsNullOrWhiteSpace(_recordingsDirectory))
        {
            SelectedRecording = null;
            RecordingLibraryStatus = NoRecordingsDirectoryMessage;
            return;
        }

        foreach (
            var recording in _allRecordings.Where(recording =>
                recording.Category == SelectedRecordingCategory.Category
            )
        )
        {
            Recordings.Add(recording);
        }

        var preferredRecording = string.IsNullOrWhiteSpace(preferredSelectionPath)
            ? null
            : Recordings.FirstOrDefault(recording =>
                PathsEqual(recording.Path, preferredSelectionPath)
            );
        var existingSelection = preferMostRecent
            ? null
            : Recordings.FirstOrDefault(recording =>
                PathsEqual(recording.Path, existingSelectionPath)
            );
        SelectedRecording = preferredRecording ?? existingSelection ?? Recordings.FirstOrDefault();
        UpdateRecordingLibraryStatus();
    }

    private void UpdateCategoryCounts()
    {
        foreach (var tab in RecordingCategories)
        {
            tab.Count = _allRecordings.Count(recording => recording.Category == tab.Category);
        }
    }

    private void SelectRecordingCategory(RecordingListCategory category)
    {
        var tab = GetRecordingCategoryTab(category);

        if (tab is not null)
        {
            SelectedRecordingCategory = tab;
        }
    }

    private RecordingCategoryTab? GetRecordingCategoryTab(RecordingListCategory category)
    {
        return RecordingCategories.FirstOrDefault(tab => tab.Category == category);
    }

    private void NotifyRecordingColumnHeadersChanged()
    {
        OnPropertyChanged(nameof(ActivityColumnHeader));
        OnPropertyChanged(nameof(ContextColumnHeader));
        OnPropertyChanged(nameof(ResultColumnHeader));
        OnPropertyChanged(nameof(DurationColumnHeader));
        OnPropertyChanged(nameof(IsContextColumnVisible));
        OnPropertyChanged(nameof(IsResultColumnVisible));
        OnPropertyChanged(nameof(IsDurationColumnVisible));
        OnPropertyChanged(nameof(ContextColumnWidth));
        OnPropertyChanged(nameof(ResultColumnWidth));
        OnPropertyChanged(nameof(DurationColumnWidth));
    }

    private static RecordingListColumnHeaders GetColumnHeaders(RecordingListCategory category)
    {
        return category switch
        {
            RecordingListCategory.ChallengeMode => new RecordingListColumnHeaders(
                "Dungeon",
                "Key",
                "Result",
                "Length",
                true,
                true,
                true
            ),
            RecordingListCategory.RaidEncounter => new RecordingListColumnHeaders(
                "Boss",
                "Difficulty",
                "Result",
                "Pull Time",
                true,
                true,
                true
            ),
            _ => new RecordingListColumnHeaders(
                "Recording",
                string.Empty,
                string.Empty,
                "Length",
                false,
                false,
                true
            ),
        };
    }

    private async Task SaveSelectedRecordingCategoryAsync(RecordingListCategory category)
    {
        try
        {
            await _saveSelectedRecordingCategory(category);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The visual preference can safely remain in-memory during shutdown.
        }
    }

    private static string FormatStartedAt(RecordingCatalogFile file)
    {
        var startedAtUtc =
            file.RaidEncounter?.EncounterStartedAtUtc
            ?? file.ChallengeMode?.ChallengeStartedAtUtc
            ?? file.StartedAtUtc;

        return startedAtUtc is null
            ? MissingMetadataValue
            : $"{startedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private static RecordingListCategory GetRecordingCategory(RecordingCatalogFile file)
    {
        return file.Kind switch
        {
            RecordingCatalogKind.ChallengeMode => RecordingListCategory.ChallengeMode,
            RecordingCatalogKind.Encounter => RecordingListCategory.RaidEncounter,
            _ => RecordingListCategory.Manual,
        };
    }

    private static string GetActivity(RecordingCatalogFile file, string displayName)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            return raidEncounter.EncounterName;
        }

        if (file.ChallengeMode is { } challengeMode)
        {
            return challengeMode.DungeonName;
        }

        return file.Kind switch
        {
            RecordingCatalogKind.Encounter => "Unknown encounter",
            RecordingCatalogKind.ChallengeMode => "Mythic+ recording",
            RecordingCatalogKind.Manual => string.IsNullOrWhiteSpace(displayName)
                ? "Manual recording"
                : displayName,
            _ => string.IsNullOrWhiteSpace(displayName) ? "Recording" : displayName,
        };
    }

    private static string GetActivityDetail(RecordingCatalogFile file)
    {
        if (file.ChallengeMode is { AffixIds.Count: > 0 } challengeMode)
        {
            return $"Affix IDs {string.Join(", ", challengeMode.AffixIds)}";
        }

        return string.Empty;
    }

    private static string FormatContext(RecordingCatalogFile file)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            return raidEncounter.DifficultyId switch
            {
                NormalRaidDifficultyId => "Normal",
                HeroicRaidDifficultyId => "Heroic",
                MythicRaidDifficultyId => "Mythic",
                RaidFinderDifficultyId => "Raid Finder",
                FlexibleMythicRaidDifficultyId => "Mythic",
                _ => $"Difficulty {raidEncounter.DifficultyId}",
            };
        }

        return file.ChallengeMode is { } challengeMode ? $"+{challengeMode.KeystoneLevel}"
            : file.Kind == RecordingCatalogKind.Manual ? "Manual"
            : MissingMetadataValue;
    }

    private static string FormatResult(RecordingCatalogFile file)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            return raidEncounter.Outcome switch
            {
                RaidEncounterOutcome.Kill => "Kill",
                RaidEncounterOutcome.Wipe => "Wipe",
                _ => "Unknown",
            };
        }

        if (file.ChallengeMode is { } challengeMode)
        {
            return challengeMode.Outcome switch
            {
                ChallengeModeOutcome.Timed => "Timed",
                ChallengeModeOutcome.Depleted => "Depleted",
                _ => "Unknown",
            };
        }

        return file.Kind == RecordingCatalogKind.Manual ? "Saved" : MissingMetadataValue;
    }

    private static string FormatActivityDuration(RecordingCatalogFile file)
    {
        if (file.RaidEncounter is { } raidEncounter)
        {
            var duration =
                raidEncounter.DurationMilliseconds is { } durationMilliseconds
                    ? TimeSpan.FromMilliseconds(Math.Max(0, durationMilliseconds))
                : raidEncounter.EncounterEndedAtUtc is { } encounterEndedAt
                    ? encounterEndedAt - raidEncounter.EncounterStartedAtUtc
                : (TimeSpan?)null;

            return FormatNullableDuration(duration);
        }

        var recordingDuration =
            file.StartedAtUtc is { } recordingStartedAt && file.EndedAtUtc is { } recordingEndedAt
                ? recordingEndedAt - recordingStartedAt
                : (TimeSpan?)null;

        return FormatNullableDuration(recordingDuration);
    }

    private static string FormatNullableDuration(TimeSpan? duration)
    {
        return duration is null
            ? MissingMetadataValue
            : RecordingTimeFormatter.FormatPlaybackTime(
                duration.Value < TimeSpan.Zero ? TimeSpan.Zero : duration.Value
            );
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRecording))]
    private Task DeleteSelectedRecordingAsync()
    {
        return ExecuteCommandAsync(DeleteSelectedRecordingCoreAsync);
    }

    private async Task DeleteSelectedRecordingCoreAsync()
    {
        var recording = SelectedRecording;

        if (recording is null || !_confirmPermanentDelete(recording))
        {
            return;
        }

        var removal = RemoveRecording(recording);
        SelectedRecording = null;
        UpdateRecordingLibraryStatus();

        try
        {
            SelectRecordingNear(removal.VisibleIndex);
            await _deleteRecording(recording.Id);
            CommandMessage = "Recording deleted.";
        }
        catch (Exception exception)
        {
            RestoreRecording(recording, removal);
            CommandMessage = $"Could not delete recording: {exception.Message}";
        }
    }

    private RecordingRemoval RemoveRecording(RecordingListItem recording)
    {
        var visibleIndex = FindRecordingIndex(recording.Id);
        if (visibleIndex >= 0)
        {
            Recordings.RemoveAt(visibleIndex);
        }

        var allIndex = FindAllRecordingIndex(recording.Id);
        if (allIndex >= 0)
        {
            _allRecordings.RemoveAt(allIndex);
        }

        UpdateCategoryCounts();
        return new RecordingRemoval(visibleIndex, allIndex);
    }

    private void RestoreRecording(RecordingListItem recording, RecordingRemoval removal)
    {
        if (FindAllRecordingIndex(recording.Id) >= 0)
        {
            return;
        }

        var allInsertIndex =
            removal.AllIndex < 0 || removal.AllIndex > _allRecordings.Count
                ? _allRecordings.Count
                : removal.AllIndex;
        _allRecordings.Insert(allInsertIndex, recording);

        if (recording.Category == SelectedRecordingCategory.Category)
        {
            var visibleInsertIndex =
                removal.VisibleIndex < 0 || removal.VisibleIndex > Recordings.Count
                    ? Recordings.Count
                    : removal.VisibleIndex;
            Recordings.Insert(visibleInsertIndex, recording);
        }

        UpdateCategoryCounts();
        UpdateRecordingLibraryStatus();
    }

    private void SelectRecordingNear(int removedIndex)
    {
        if (SelectedRecording is not null || Recordings.Count == 0)
        {
            return;
        }

        var selectedIndex = removedIndex < 0 ? 0 : Math.Min(removedIndex, Recordings.Count - 1);
        SelectedRecording = Recordings[selectedIndex];
    }

    private int FindRecordingIndex(Guid id)
    {
        for (var index = 0; index < Recordings.Count; index++)
        {
            if (Recordings[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindAllRecordingIndex(Guid id)
    {
        for (var index = 0; index < _allRecordings.Count; index++)
        {
            if (_allRecordings[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private RecordingListItem? FindAllRecordingByPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : _allRecordings.FirstOrDefault(recording => PathsEqual(recording.Path, path));
    }

    private void UpdateRecordingLibraryStatus()
    {
        if (Recordings.Count > 0)
        {
            RecordingLibraryStatus = string.Empty;
            return;
        }

        RecordingLibraryStatus =
            _allRecordings.Count == 0
                ? NoRecordingsMessage
                : $"No {SelectedRecordingCategory.Title} recordings found yet.";
    }

    private sealed record RecordingRemoval(int VisibleIndex, int AllIndex);

    private sealed record RecordingListColumnHeaders(
        string Activity,
        string Context,
        string Result,
        string Duration,
        bool IsContextVisible,
        bool IsResultVisible,
        bool IsDurationVisible
    );

    private Task ToggleManualRecordingAsync()
    {
        return _recording.State == RecordingCoordinatorState.Recording
            ? StopManualAsync()
            : StartManualAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunManualCommand))]
    private Task ManualRecordingAsync()
    {
        return ExecuteCommandAsync(ToggleManualRecordingAsync);
    }

    private async Task ExecuteCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception exception)
        {
            HandleCommandFailure(exception);
        }
    }

    private async Task StartManualAsync()
    {
        CommandMessage = GetCommandMessage(
            await _startManual(),
            "Manual recording started.",
            _recording.LastFailure
        );
    }

    private async Task StopManualAsync()
    {
        CommandMessage = GetCommandMessage(
            await _stopManual(),
            "Recording stopped.",
            _recording.LastFailure
        );
    }

    [RelayCommand]
    private async Task OpenRecordingsFolderAsync()
    {
        try
        {
            await _openRecordingsFolder();
            CommandMessage = null;
        }
        catch (Exception exception)
        {
            CommandMessage = $"Could not open recordings folder: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsFailureVisible))]
    private void DismissFailure()
    {
        _dismissedFailure = _recording.LastFailure;
        OnPropertyChanged(nameof(IsFailureVisible));
        DismissFailureCommand.NotifyCanExecuteChanged();
    }

    private void HandleCommandFailure(Exception exception)
    {
        CommandMessage = $"Command failed: {exception.Message}";
    }

    private static string GetStateTitle(
        RecordingCoordinatorStatus recording,
        WowProcessStatus wowProcess
    )
    {
        return recording.State switch
        {
            RecordingCoordinatorState.Starting => "Processing",
            RecordingCoordinatorState.Recording => "Recording",
            RecordingCoordinatorState.Stopping => "Processing",
            _ when !wowProcess.IsWindowAvailable => "Waiting",
            _ => "Ready",
        };
    }

    private static string GetReadinessDetail(
        RecordingCoordinatorStatus recording,
        CombatLogReaderStatus combatLog,
        WowProcessStatus wowProcess
    )
    {
        if (recording.State == RecordingCoordinatorState.Starting)
        {
            return JoinSentences("WoW is running.", "Starting recording.");
        }

        if (recording.State == RecordingCoordinatorState.Recording)
        {
            return recording.Owner == RecordingOwner.Manual
                ? JoinSentences("WoW is running.", "Manual recording is active.")
                : JoinSentences("WoW is running.", "Automatic recording is active.");
        }

        if (recording.State == RecordingCoordinatorState.Stopping)
        {
            return "WoW recording is being saved.";
        }

        if (!wowProcess.IsWindowAvailable)
        {
            return wowProcess.State == WowProcessState.WaitingForWindow
                ? JoinSentences("World of Warcraft is running.", "No game window is available yet.")
                : "Start World of Warcraft to enable manual and automatic recording.";
        }

        if (combatLog.LastFileSystemError is not null)
        {
            var message = string.IsNullOrWhiteSpace(combatLog.LastFileSystemError.Message)
                ? combatLog.LastFileSystemError.GetType().Name
                : combatLog.LastFileSystemError.Message.TrimEnd('.', ' ', '\t', '\r', '\n');
            return JoinSentences(
                "WoW is running.",
                $"Manual recording is ready, but automatic recording cannot read combat logs: {message}."
            );
        }

        return combatLog.State switch
        {
            CombatLogReaderState.WaitingForWow =>
                "Start World of Warcraft to enable manual and automatic recording.",
            CombatLogReaderState.WaitingForLogsDirectory => JoinSentences(
                "WoW is running.",
                "Manual recording is ready.",
                "Automatic recording is waiting for the WoW logs folder."
            ),
            CombatLogReaderState.WaitingForCombatLog => JoinSentences(
                "WoW is running.",
                "Manual recording is ready.",
                "Enable combat logging in WoW for automatic recording."
            ),
            CombatLogReaderState.SwitchingCombatLog => JoinSentences(
                "WoW is running.",
                "Automatic recording is switching combat logs.",
                "Manual recording is ready."
            ),
            CombatLogReaderState.ReadingCombatLog => JoinSentences(
                "WoW is running.",
                "Manual and automatic recording are ready."
            ),
            _ => JoinSentences("WoW is running.", "Manual recording is ready."),
        };
    }

    private static RecordingStatusHealth GetStatusHealth(
        RecordingCoordinatorStatus recording,
        CombatLogReaderStatus combatLog,
        WowProcessStatus wowProcess
    )
    {
        if (recording.LastFailure is not null && !IsTargetUnavailableFailure(recording.LastFailure))
        {
            return RecordingStatusHealth.AttentionNeeded;
        }

        if (recording.State != RecordingCoordinatorState.Idle)
        {
            return RecordingStatusHealth.Active;
        }

        if (!wowProcess.IsWindowAvailable)
        {
            return RecordingStatusHealth.Waiting;
        }

        if (combatLog.LastFileSystemError is not null)
        {
            return RecordingStatusHealth.AttentionNeeded;
        }

        return IsAutomaticRecordingReady(combatLog)
            ? RecordingStatusHealth.Ready
            : RecordingStatusHealth.ManualOnly;
    }

    private static bool IsAutomaticRecordingReady(CombatLogReaderStatus combatLog)
    {
        return combatLog.State == CombatLogReaderState.ReadingCombatLog
            && combatLog.LastFileSystemError is null;
    }

    private static string JoinSentences(params string[] sentences)
    {
        return string.Join(Environment.NewLine, sentences);
    }

    private static string? GetCommandMessage(
        RecordingCommandResult result,
        string successMessage,
        Exception? lastFailure = null
    )
    {
        if (IsTargetUnavailableFailure(lastFailure))
        {
            return TargetUnavailableMessage;
        }

        return result switch
        {
            RecordingCommandResult.Started or RecordingCommandResult.Stopped => successMessage,
            RecordingCommandResult.AlreadyActive => "A recording is already active.",
            RecordingCommandResult.NoActiveRecording => "There is no active recording to stop.",
            RecordingCommandResult.Suppressed => "Automatic recording is temporarily suppressed.",
            RecordingCommandResult.OwnerMismatch =>
                "The active recording could not be stopped by this command.",
            RecordingCommandResult.TargetUnavailable => TargetUnavailableMessage,
            RecordingCommandResult.TimedOut => "The recorder did not respond in time.",
            RecordingCommandResult.Failed => lastFailure is null
                ? "The recording command failed."
                : GetFailureCommandMessage(lastFailure),
            _ => null,
        };
    }

    private static string GetFailureCommandMessage(Exception exception)
    {
        if (IsTargetUnavailableFailure(exception))
        {
            return TargetUnavailableMessage;
        }

        if (RecordingFailureClassifier.IsOutputUnavailable(exception))
        {
            return exception.Message;
        }

        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;

        return $"The recording command failed: {message}";
    }

    private static bool IsTargetUnavailableFailure(Exception? exception)
    {
        return RecordingFailureClassifier.IsTargetUnavailable(exception);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }
}
