using System.Collections.ObjectModel;
using System.IO;

namespace PullWatch;

public sealed class DashboardViewModel : ObservableObject
{
    private const string TargetUnavailableMessage = "World of Warcraft is not running.";
    private const string NoRecordingsDirectoryMessage = "Choose a recordings directory in settings to review videos here.";
    private const string NoRecordingsMessage = "No finished .mp4 recordings found yet.";

    private readonly Func<CancellationToken, Task<RecordingCommandResult>> _startManual;
    private readonly Func<CancellationToken, Task<RecordingCommandResult>> _stopManual;
    private readonly Func<Task> _openRecordingsFolder;
    private RecordingCoordinatorStatus _recording;
    private CombatLogReaderStatus _combatLog;
    private string? _recordingsDirectory;
    private Exception? _dismissedFailure;
    private string _duration = "00:00:00";
    private string _recordingLibraryStatus = NoRecordingsDirectoryMessage;
    private string? _commandMessage;
    private RecordingListItem? _selectedRecording;
    private int _knownSavedCount;

    public DashboardViewModel(
        ApplicationStatus initialStatus,
        Func<CancellationToken, Task<RecordingCommandResult>> startManual,
        Func<CancellationToken, Task<RecordingCommandResult>> stopManual,
        Func<Task> openRecordingsFolder)
    {
        _recording = initialStatus.Recording;
        _combatLog = initialStatus.CombatLog;
        _recordingsDirectory = initialStatus.EffectiveSettings?.RecordingsDirectory;
        _knownSavedCount = initialStatus.Recording.Statistics.SavedCount;
        _startManual = startManual;
        _stopManual = stopManual;
        _openRecordingsFolder = openRecordingsFolder;
        ManualRecordingCommand = new AsyncRelayCommand(
            ToggleManualRecordingAsync,
            () => CanRunManualCommand,
            HandleCommandFailure);
        OpenRecordingsFolderCommand = new AsyncRelayCommand(
            OpenRecordingsFolderAsync,
            onException: HandleCommandFailure);
        DismissFailureCommand = new RelayCommand(DismissFailure, () => IsFailureVisible);
        ApplyStatus(initialStatus);
        RefreshRecordings();
    }

    public ObservableCollection<RecordingListItem> Recordings { get; } = new();

    public AsyncRelayCommand ManualRecordingCommand { get; }

    public AsyncRelayCommand OpenRecordingsFolderCommand { get; }

    public RelayCommand DismissFailureCommand { get; }

    public string StateTitle => GetStateTitle(_recording.State);

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
            }
        }
    }

    public string RecordingLibraryStatus
    {
        get => _recordingLibraryStatus;
        private set => SetProperty(ref _recordingLibraryStatus, value);
    }

    public bool IsPlayerPlaceholderVisible => SelectedRecording is null;

    public string RecorderHealth => _recording.LastFailure is not null &&
                                    !IsTargetUnavailableFailure(_recording.LastFailure)
        ? "Attention needed"
        : _recording.State == RecordingCoordinatorState.Idle
            ? "Idle"
            : "Active";

    public string? FailureMessage => IsTargetUnavailableFailure(_recording.LastFailure)
        ? null
        : _recording.LastFailure?.Message;

    public string? CommandMessage
    {
        get => _commandMessage;
        private set => SetProperty(ref _commandMessage, value);
    }

    public bool IsFailureVisible =>
        _recording.LastFailure is not null &&
        !IsTargetUnavailableFailure(_recording.LastFailure) &&
        !ReferenceEquals(_recording.LastFailure, _dismissedFailure);

    public bool IsManualStopMode => _recording.State == RecordingCoordinatorState.Recording;

    public string ManualRecordingButtonText => IsManualStopMode
        ? "Manual stop"
        : "Manual start";

    public void ApplyStatus(ApplicationStatus status)
    {
        var previousDirectory = _recordingsDirectory;
        var previousSavedCount = _knownSavedCount;
        _recording = status.Recording;
        _combatLog = status.CombatLog;
        _recordingsDirectory = status.EffectiveSettings?.RecordingsDirectory;
        _knownSavedCount = status.Recording.Statistics.SavedCount;

        if (CommandMessage == "The recording command failed." &&
            _recording.LastFailure is not null)
        {
            CommandMessage = GetFailureCommandMessage(_recording.LastFailure);
        }

        UpdateDuration();
        if (!PathsEqual(previousDirectory, _recordingsDirectory) ||
            previousSavedCount != _knownSavedCount)
        {
            RefreshRecordings();
        }

        OnAllPropertiesChanged();
        ManualRecordingCommand.NotifyCanExecuteChanged();
        DismissFailureCommand.NotifyCanExecuteChanged();
    }

    public void UpdateDuration(DateTimeOffset? now = null)
    {
        Duration = _recording.Context is null ||
                   _recording.State == RecordingCoordinatorState.Idle
            ? "00:00:00"
            : FormatDuration((now ?? DateTimeOffset.Now) - _recording.Context.StartedAt);
    }

    private bool CanRunManualCommand =>
        _recording.State is RecordingCoordinatorState.Idle or RecordingCoordinatorState.Recording;

    internal static string FormatDuration(TimeSpan duration)
    {
        var value = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return value.TotalHours >= 100
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    internal void RefreshRecordings()
    {
        var selectedPath = SelectedRecording?.Path;
        Recordings.Clear();

        if (string.IsNullOrWhiteSpace(_recordingsDirectory))
        {
            SelectedRecording = null;
            RecordingLibraryStatus = NoRecordingsDirectoryMessage;
            return;
        }

        try
        {
            foreach (var recording in DiscoverRecordings(_recordingsDirectory))
            {
                Recordings.Add(recording);
            }

            SelectedRecording =
                Recordings.FirstOrDefault(recording => PathsEqual(recording.Path, selectedPath)) ??
                Recordings.FirstOrDefault();
            RecordingLibraryStatus = Recordings.Count == 0
                ? NoRecordingsMessage
                : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SelectedRecording = null;
            RecordingLibraryStatus = $"Could not read recordings folder: {exception.Message}";
        }
    }

    internal static IReadOnlyList<RecordingListItem> DiscoverRecordings(string recordingsDirectory)
    {
        if (!Directory.Exists(recordingsDirectory))
        {
            return [];
        }

        return new DirectoryInfo(recordingsDirectory)
            .EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new RecordingListItem(
                file.FullName,
                System.IO.Path.GetFileNameWithoutExtension(file.Name),
                file.LastWriteTime,
                file.Length))
            .ToList();
    }

    private Task ToggleManualRecordingAsync()
    {
        return _recording.State == RecordingCoordinatorState.Recording
            ? StopManualAsync()
            : StartManualAsync();
    }

    private async Task StartManualAsync()
    {
        CommandMessage = GetCommandMessage(
            await _startManual(CancellationToken.None),
            "Manual recording started.",
            _recording.LastFailure);
    }

    private async Task StopManualAsync()
    {
        CommandMessage = GetCommandMessage(
            await _stopManual(CancellationToken.None),
            "Recording stopped.",
            _recording.LastFailure);
    }

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

    private static string GetStateTitle(RecordingCoordinatorState state)
    {
        return state switch
        {
            RecordingCoordinatorState.Starting => "Starting recording",
            RecordingCoordinatorState.Recording => "Recording",
            RecordingCoordinatorState.Stopping => "Finalizing recording",
            _ => "Ready to record"
        };
    }

    private static string? GetCommandMessage(
        RecordingCommandResult result,
        string successMessage,
        Exception? lastFailure = null)
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
            RecordingCommandResult.OwnerMismatch => "The active recording could not be stopped by this command.",
            RecordingCommandResult.TargetUnavailable => TargetUnavailableMessage,
            RecordingCommandResult.TimedOut => "The recorder did not respond in time.",
            RecordingCommandResult.Failed => lastFailure is null
                ? "The recording command failed."
                : GetFailureCommandMessage(lastFailure),
            _ => null
        };
    }

    private static string GetFailureCommandMessage(Exception exception)
    {
        if (IsTargetUnavailableFailure(exception))
        {
            return TargetUnavailableMessage;
        }

        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;

        return $"The recording command failed: {message}";
    }

    private static bool IsTargetUnavailableFailure(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        var text = exception.ToString();
        return text.Contains("World of Warcraft", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("window", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }
}
