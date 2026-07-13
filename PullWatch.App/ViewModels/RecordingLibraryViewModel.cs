using System.Collections.ObjectModel;
using System.Windows;

namespace PullWatch;

internal sealed class RecordingLibraryViewModel : ObservableObject
{
    private const string NoDirectoryMessage =
        "Choose a recordings directory in settings to review videos here.";
    private const string NoRecordingsMessage = "No finished .mp4 recordings found yet.";

    private readonly Func<string, Task<IReadOnlyList<RecordingCatalogFile>>> _loadRecordings;
    private readonly Func<Guid, Task> _deleteRecording;
    private readonly Func<Guid, bool, Task> _setRecordingFavorite;
    private readonly Func<RecordingListItem, bool> _confirmPermanentDelete;
    private readonly Func<RecordingListCategory, Task> _saveSelectedCategory;
    private readonly Action<string?> _setCommandMessage;
    private readonly Action<string?> _setFavoriteError;
    private readonly List<RecordingListItem> _allRecordings = new();
    private ActiveRefresh? _activeRefresh;
    private string? _recordingsDirectory;
    private RecordingListItem? _selectedRecording;
    private RecordingCategoryTab _selectedCategory;
    private string _status = NoDirectoryMessage;

    public RecordingLibraryViewModel(
        string? recordingsDirectory,
        RecordingListCategory selectedCategory,
        Func<string, Task<IReadOnlyList<RecordingCatalogFile>>> loadRecordings,
        Func<Guid, Task> deleteRecording,
        Func<Guid, bool, Task> setRecordingFavorite,
        Func<RecordingListItem, bool> confirmPermanentDelete,
        Func<RecordingListCategory, Task> saveSelectedCategory,
        Action<string?> setCommandMessage,
        Action<string?> setFavoriteError
    )
    {
        _recordingsDirectory = recordingsDirectory;
        _loadRecordings = loadRecordings;
        _deleteRecording = deleteRecording;
        _setRecordingFavorite = setRecordingFavorite;
        _confirmPermanentDelete = confirmPermanentDelete;
        _saveSelectedCategory = saveSelectedCategory;
        _setCommandMessage = setCommandMessage;
        _setFavoriteError = setFavoriteError;
        RecordingCategories =
        [
            new RecordingCategoryTab(RecordingListCategory.ChallengeMode, "Mythic+"),
            new RecordingCategoryTab(RecordingListCategory.RaidEncounter, "Raid"),
            new RecordingCategoryTab(RecordingListCategory.Manual, "Manual"),
        ];
        _selectedCategory = GetCategoryTab(selectedCategory) ?? RecordingCategories[0];
    }

    public ObservableCollection<RecordingCategoryTab> RecordingCategories { get; }

    public ObservableCollection<RecordingListItem> Recordings { get; } = new();

    public string ActivityColumnHeader => CurrentColumnHeaders.Activity;

    public string PullNumberColumnHeader => "Pull #";

    public string ContextColumnHeader => CurrentColumnHeaders.Context;

    public string ResultColumnHeader => CurrentColumnHeaders.Result;

    public string DurationColumnHeader => CurrentColumnHeaders.Duration;

    public bool IsPullNumberColumnVisible =>
        SelectedRecordingCategory.Category == RecordingListCategory.RaidEncounter;

    public bool IsContextColumnVisible => CurrentColumnHeaders.IsContextVisible;

    public bool IsResultColumnVisible => CurrentColumnHeaders.IsResultVisible;

    public bool IsDurationColumnVisible => CurrentColumnHeaders.IsDurationVisible;

    public GridLength PullNumberColumnWidth =>
        RecordingListColumnLayout.PullNumber(IsPullNumberColumnVisible);

    public GridLength ContextColumnWidth =>
        RecordingListColumnLayout.Context(IsContextColumnVisible);

    public GridLength ResultColumnWidth => RecordingListColumnLayout.Result(IsResultColumnVisible);

    public GridLength DurationColumnWidth =>
        RecordingListColumnLayout.Duration(IsDurationColumnVisible);

    public RecordingCategoryTab SelectedRecordingCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value))
            {
                return;
            }

            NotifyColumnPresentationChanged();
            ApplyFilter();
            _ = SaveSelectedCategoryAsync(value.Category);
        }
    }

    public RecordingListItem? SelectedRecording
    {
        get => _selectedRecording;
        set => SetProperty(ref _selectedRecording, value);
    }

    public string RecordingLibraryStatus
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool UpdateDirectory(string? recordingsDirectory)
    {
        if (PathsEqual(_recordingsDirectory, recordingsDirectory))
        {
            return false;
        }

        _recordingsDirectory = recordingsDirectory;
        return true;
    }

    public async Task RefreshAsync(
        string? preferredSelectionPath = null,
        bool preferMostRecent = false
    )
    {
        var refresh = new ActiveRefresh();
        _activeRefresh = refresh;
        var recordingsDirectory = _recordingsDirectory;
        var existingSelectionPath = SelectedRecording?.Path;

        if (string.IsNullOrWhiteSpace(recordingsDirectory))
        {
            Clear(NoDirectoryMessage);
            CompleteRefresh(refresh);
            return;
        }

        try
        {
            var recordings = await _loadRecordings(recordingsDirectory);
            var recordingItems = RecordingListItemFactory.Create(recordings);

            if (!IsCurrentRefresh(refresh, recordingsDirectory))
            {
                return;
            }

            recordingItems = ExcludeDeletedRecordings(recordingItems, refresh);
            ApplyFavoriteChanges(recordingItems, refresh);
            _allRecordings.Clear();
            _allRecordings.AddRange(recordingItems);
            UpdateCategoryCounts();

            var preferredRecording = FindAllByPath(preferredSelectionPath);
            if (
                preferredRecording is not null
                && preferredRecording.Category != SelectedRecordingCategory.Category
            )
            {
                SelectCategory(preferredRecording.Category);
            }

            ApplyFilter(
                preferredRecording?.Path,
                preferMostRecent,
                preferMostRecent ? null : existingSelectionPath
            );
        }
        catch (Exception exception)
        {
            if (IsCurrentRefresh(refresh, recordingsDirectory))
            {
                Clear($"Could not read recordings catalog: {exception.Message}");
            }
        }
        finally
        {
            CompleteRefresh(refresh);
        }
    }

    public async Task DeleteSelectedRecordingAsync()
    {
        var recording = SelectedRecording;

        if (recording is null || !_confirmPermanentDelete(recording))
        {
            return;
        }

        _activeRefresh = null;
        var removal = Remove(recording);
        SelectedRecording = null;
        UpdateStatus();

        try
        {
            SelectNear(removal.VisibleIndex);
            await _deleteRecording(recording.Id);

            if (_activeRefresh is { } refresh)
            {
                refresh.DeletedRecordingIds.Add(recording.Id);
            }

            RemoveRestoredRecording(recording.Id);
            _setCommandMessage("Recording deleted.");
        }
        catch (Exception exception)
        {
            Restore(recording, removal);
            _setCommandMessage($"Could not delete recording: {exception.Message}");
        }
    }

    public async Task ToggleFavoriteAsync(RecordingListItem recording)
    {
        var isFavorite = !recording.IsFavorite;

        try
        {
            await _setRecordingFavorite(recording.Id, isFavorite);
        }
        catch (Exception exception)
        {
            var action = isFavorite ? "add recording to" : "remove recording from";
            var message = $"Could not {action} favourites: {exception.Message}";
            _setCommandMessage(message);
            _setFavoriteError(message);
            return;
        }

        _setFavoriteError(null);
        if (_activeRefresh is { } refresh)
        {
            refresh.FavoriteChanges[recording.Id] = isFavorite;
        }

        var allIndex = FindIndex(_allRecordings, recording.Id);
        if (allIndex < 0)
        {
            return;
        }

        _allRecordings[allIndex].SetFavorite(isFavorite);

        _setCommandMessage(
            isFavorite ? "Recording added to favourites." : "Recording removed from favourites."
        );
    }

    private static void ApplyFavoriteChanges(
        IReadOnlyList<RecordingListItem> recordings,
        ActiveRefresh refresh
    )
    {
        foreach (var recording in recordings)
        {
            if (refresh.FavoriteChanges.TryGetValue(recording.Id, out var isFavorite))
            {
                recording.SetFavorite(isFavorite);
            }
        }
    }

    private static IReadOnlyList<RecordingListItem> ExcludeDeletedRecordings(
        IReadOnlyList<RecordingListItem> recordings,
        ActiveRefresh refresh
    )
    {
        return refresh.DeletedRecordingIds.Count == 0
            ? recordings
            : recordings
                .Where(recording => !refresh.DeletedRecordingIds.Contains(recording.Id))
                .ToList();
    }

    private RecordingListColumnHeaders CurrentColumnHeaders =>
        GetColumnHeaders(SelectedRecordingCategory.Category);

    private bool IsCurrentRefresh(ActiveRefresh refresh, string recordingsDirectory)
    {
        return ReferenceEquals(refresh, _activeRefresh)
            && PathsEqual(recordingsDirectory, _recordingsDirectory);
    }

    private void CompleteRefresh(ActiveRefresh refresh)
    {
        if (ReferenceEquals(refresh, _activeRefresh))
        {
            _activeRefresh = null;
        }
    }

    private void Clear(string status)
    {
        _allRecordings.Clear();
        Recordings.Clear();
        UpdateCategoryCounts();
        SelectedRecording = null;
        RecordingLibraryStatus = status;
    }

    private void ApplyFilter(
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
            RecordingLibraryStatus = NoDirectoryMessage;
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
        UpdateStatus();
    }

    private void UpdateCategoryCounts()
    {
        foreach (var tab in RecordingCategories)
        {
            tab.Count = _allRecordings.Count(recording => recording.Category == tab.Category);
        }
    }

    private void SelectCategory(RecordingListCategory category)
    {
        var tab = GetCategoryTab(category);

        if (tab is not null)
        {
            SelectedRecordingCategory = tab;
        }
    }

    private RecordingCategoryTab? GetCategoryTab(RecordingListCategory category)
    {
        return RecordingCategories.FirstOrDefault(tab => tab.Category == category);
    }

    private void NotifyColumnPresentationChanged()
    {
        OnPropertyChanged(nameof(ActivityColumnHeader));
        OnPropertyChanged(nameof(PullNumberColumnHeader));
        OnPropertyChanged(nameof(ContextColumnHeader));
        OnPropertyChanged(nameof(ResultColumnHeader));
        OnPropertyChanged(nameof(DurationColumnHeader));
        OnPropertyChanged(nameof(IsPullNumberColumnVisible));
        OnPropertyChanged(nameof(IsContextColumnVisible));
        OnPropertyChanged(nameof(IsResultColumnVisible));
        OnPropertyChanged(nameof(IsDurationColumnVisible));
        OnPropertyChanged(nameof(PullNumberColumnWidth));
        OnPropertyChanged(nameof(ContextColumnWidth));
        OnPropertyChanged(nameof(ResultColumnWidth));
        OnPropertyChanged(nameof(DurationColumnWidth));
    }

    private async Task SaveSelectedCategoryAsync(RecordingListCategory category)
    {
        try
        {
            await _saveSelectedCategory(category);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The visual preference can safely remain in-memory during shutdown.
        }
    }

    private RecordingRemoval Remove(RecordingListItem recording)
    {
        var visibleIndex = FindIndex(Recordings, recording.Id);
        if (visibleIndex >= 0)
        {
            Recordings.RemoveAt(visibleIndex);
        }

        var allIndex = FindIndex(_allRecordings, recording.Id);
        if (allIndex >= 0)
        {
            _allRecordings.RemoveAt(allIndex);
        }

        UpdateCategoryCounts();
        return new RecordingRemoval(visibleIndex, allIndex);
    }

    private void RemoveRestoredRecording(Guid id)
    {
        var allIndex = FindIndex(_allRecordings, id);
        if (allIndex < 0)
        {
            return;
        }

        var wasSelected = SelectedRecording?.Id == id;
        var removal = Remove(_allRecordings[allIndex]);
        if (wasSelected)
        {
            SelectedRecording = null;
            SelectNear(removal.VisibleIndex);
        }

        UpdateStatus();
    }

    private void Restore(RecordingListItem recording, RecordingRemoval removal)
    {
        if (FindIndex(_allRecordings, recording.Id) >= 0)
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
        UpdateStatus();
    }

    private void SelectNear(int removedIndex)
    {
        if (SelectedRecording is not null || Recordings.Count == 0)
        {
            return;
        }

        var selectedIndex = removedIndex < 0 ? 0 : Math.Min(removedIndex, Recordings.Count - 1);
        SelectedRecording = Recordings[selectedIndex];
    }

    private static int FindIndex(IReadOnlyList<RecordingListItem> recordings, Guid id)
    {
        for (var index = 0; index < recordings.Count; index++)
        {
            if (recordings[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private RecordingListItem? FindAllByPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : _allRecordings.FirstOrDefault(recording => PathsEqual(recording.Path, path));
    }

    private void UpdateStatus()
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

    private static bool PathsEqual(string? left, string? right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    private sealed record RecordingRemoval(int VisibleIndex, int AllIndex);

    private sealed class ActiveRefresh
    {
        public Dictionary<Guid, bool> FavoriteChanges { get; } = [];

        public HashSet<Guid> DeletedRecordingIds { get; } = [];
    }

    private sealed record RecordingListColumnHeaders(
        string Activity,
        string Context,
        string Result,
        string Duration,
        bool IsContextVisible,
        bool IsResultVisible,
        bool IsDurationVisible
    );
}
