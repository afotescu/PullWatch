namespace PullWatch;

public sealed class DashboardViewModel : ObservableObject
{
    private const string NoOutputPath = "An output path will appear when recording starts.";

    private readonly Func<CancellationToken, Task<RecordingCommandResult>> _startManual;
    private readonly Func<CancellationToken, Task<RecordingCommandResult>> _stopManual;
    private readonly Func<Task> _openRecordingsFolder;
    private RecordingCoordinatorStatus _recording;
    private CombatLogReaderStatus _combatLog;
    private Exception? _dismissedFailure;
    private string _duration = "00:00:00";
    private string? _commandMessage;

    public DashboardViewModel(
        ApplicationStatus initialStatus,
        Func<CancellationToken, Task<RecordingCommandResult>> startManual,
        Func<CancellationToken, Task<RecordingCommandResult>> stopManual,
        Func<Task> openRecordingsFolder)
    {
        _recording = initialStatus.Recording;
        _combatLog = initialStatus.CombatLog;
        _startManual = startManual;
        _stopManual = stopManual;
        _openRecordingsFolder = openRecordingsFolder;
        StartManualCommand = new AsyncRelayCommand(
            StartManualAsync,
            () => CanStartManual,
            HandleCommandFailure);
        StopManualCommand = new AsyncRelayCommand(
            StopManualAsync,
            () => CanStopManual,
            HandleCommandFailure);
        OpenRecordingsFolderCommand = new AsyncRelayCommand(
            OpenRecordingsFolderAsync,
            onException: HandleCommandFailure);
        DismissFailureCommand = new RelayCommand(DismissFailure, () => IsFailureVisible);
        ApplyStatus(initialStatus);
    }

    public AsyncRelayCommand StartManualCommand { get; }

    public AsyncRelayCommand StopManualCommand { get; }

    public AsyncRelayCommand OpenRecordingsFolderCommand { get; }

    public RelayCommand DismissFailureCommand { get; }

    public string StateTitle => GetStateTitle(_recording.State);

    public string StateDescription => GetStateDescription(_recording.State);

    public string RecordingDetail => GetRecordingDetail(_recording);

    public string Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    public string OutputPath => _recording.ActiveOutputPath ?? NoOutputPath;

    public string RecordingStatistics =>
        $"{_recording.Statistics.ExpectedCount} expected · {_recording.Statistics.SavedCount} saved this session";

    public string CombatLogHealth => _combatLog.LastFileSystemError is not null
        ? "Logs directory error"
        : _combatLog.State switch
        {
            CombatLogReaderState.ReadingCombatLog => "Monitoring",
            CombatLogReaderState.SwitchingCombatLog => "Switching logs",
            CombatLogReaderState.WaitingForCombatLog => "Waiting for combat log",
            _ => "Logs directory unavailable"
        };

    public string CombatLogDetail => _combatLog.LastFileSystemError?.Message
        ?? _combatLog.CurrentPath
        ?? "PullWatch will keep checking in the background.";

    public string RecorderHealth => _recording.LastFailure is not null
        ? "Attention needed"
        : _recording.State == RecordingCoordinatorState.Idle
            ? "Idle"
            : "Active";

    public string RecorderDetail => _recording.State == RecordingCoordinatorState.Idle
        ? "Recording can start when World of Warcraft is running."
        : $"Recorder is {_recording.State.ToString().ToLowerInvariant()}.";

    public string? FailureMessage => _recording.LastFailure?.Message;

    public string? CommandMessage
    {
        get => _commandMessage;
        private set => SetProperty(ref _commandMessage, value);
    }

    public bool IsFailureVisible =>
        _recording.LastFailure is not null &&
        !ReferenceEquals(_recording.LastFailure, _dismissedFailure);

    public bool CanStartManual => _recording.State == RecordingCoordinatorState.Idle;

    public bool CanStopManual => _recording.State == RecordingCoordinatorState.Recording;

    public void ApplyStatus(ApplicationStatus status)
    {
        _recording = status.Recording;
        _combatLog = status.CombatLog;

        UpdateDuration();
        OnAllPropertiesChanged();
        StartManualCommand.NotifyCanExecuteChanged();
        StopManualCommand.NotifyCanExecuteChanged();
        DismissFailureCommand.NotifyCanExecuteChanged();
    }

    public void UpdateDuration(DateTimeOffset? now = null)
    {
        Duration = _recording.Context is null ||
                   _recording.State == RecordingCoordinatorState.Idle
            ? "00:00:00"
            : FormatDuration((now ?? DateTimeOffset.Now) - _recording.Context.StartedAt);
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        var value = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return value.TotalHours >= 100
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private async Task StartManualAsync()
    {
        CommandMessage = GetCommandMessage(
            await _startManual(CancellationToken.None),
            "Manual recording started.");
    }

    private async Task StopManualAsync()
    {
        CommandMessage = GetCommandMessage(
            await _stopManual(CancellationToken.None),
            "Recording stopped.");
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

    private static string GetStateDescription(RecordingCoordinatorState state)
    {
        return state switch
        {
            RecordingCoordinatorState.Starting => "Preparing the recorder and output file.",
            RecordingCoordinatorState.Recording => "Your gameplay is being captured.",
            RecordingCoordinatorState.Stopping => "Saving the recording. Keep PullWatch open.",
            _ => "Automatic recording is active when combat logs are available."
        };
    }

    private static string GetRecordingDetail(RecordingCoordinatorStatus status)
    {
        return status.Context switch
        {
            ChallengeRecordingContext challenge =>
                $"{challenge.DungeonName} · Mythic +{challenge.Level}",
            EncounterRecordingContext encounter =>
                $"{encounter.EncounterName} · Raid encounter",
            ManualRecordingContext => "Manual recording",
            _ => "No active recording"
        };
    }

    private static string? GetCommandMessage(
        RecordingCommandResult result,
        string successMessage)
    {
        return result switch
        {
            RecordingCommandResult.Started or RecordingCommandResult.Stopped => successMessage,
            RecordingCommandResult.AlreadyActive => "A recording is already active.",
            RecordingCommandResult.NoActiveRecording => "There is no active recording to stop.",
            RecordingCommandResult.Suppressed => "Automatic recording is temporarily suppressed.",
            RecordingCommandResult.OwnerMismatch => "The active recording could not be stopped by this command.",
            RecordingCommandResult.TimedOut => "The recorder did not respond in time.",
            RecordingCommandResult.Failed => "The recording command failed.",
            _ => null
        };
    }
}
