using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace PullWatch;

public sealed partial class RecordingsViewModel : ObservableObject
{
    private const string FailureNotificationId = "recorder-failure";
    private const string FavoriteFailureNotificationId = "recording-favorite-failure";
    private const string TargetUnavailableMessage = "World of Warcraft is not running.";
    private const string VideoEncodingSetupFallbackMessage =
        "Video encoding needs to be tested before recording.";
    private const string VideoEncodingSetupRequiredSuffix =
        "Manual and automatic recording stay disabled until setup is complete.";
    private readonly Func<Task<RecordingCommandResult>> _startManual;
    private readonly Func<Task<RecordingCommandResult>> _stopManual;
    private readonly Func<Task> _testVideoEncoding;
    private readonly Func<Task> _openRecordingsFolder;
    private readonly Func<int, bool, Task<bool>> _savePlaybackAudioState;
    private readonly RecordingLibraryViewModel _library;
    private readonly NotificationCenterViewModel? _notifications;
    private RecordingCoordinatorStatus _recording;
    private CombatLogReaderStatus _combatLog;
    private WowProcessStatus _wowProcess;
    private EncoderCalibrationStatus? _videoEncoding;
    private Exception? _dismissedFailure;
    private string _duration = "00:00:00";
    private string? _commandMessage;
    private int _knownSavedCount;
    private bool _isTestingVideoEncoding;
    private int _playbackVolumePercent;
    private bool _isPlaybackMuted;
    private bool _isPlaybackAudioSavePending;
    private int _playbackAudioSaveVersion;

    public RecordingsViewModel(
        ApplicationStatus initialStatus,
        Func<Task<RecordingCommandResult>> startManual,
        Func<Task<RecordingCommandResult>> stopManual,
        Func<Task> testVideoEncoding,
        Func<string, Task<IReadOnlyList<RecordingCatalogFile>>> loadRecordings,
        Func<Guid, Task> deleteRecording,
        Func<RecordingListItem, bool> confirmPermanentDelete,
        Func<Task> openRecordingsFolder,
        Func<RecordingListCategory, Task> saveSelectedRecordingCategory,
        NotificationCenterViewModel? notifications = null,
        Func<int, bool, Task<bool>>? savePlaybackAudioState = null,
        Func<Guid, bool, Task>? setRecordingFavorite = null
    )
    {
        _recording = initialStatus.Recording;
        _combatLog = initialStatus.CombatLog;
        _wowProcess = initialStatus.WowProcess;
        _videoEncoding = initialStatus.VideoEncoding;
        _knownSavedCount = initialStatus.Recording.Statistics.SavedCount;
        _startManual = startManual;
        _stopManual = stopManual;
        _testVideoEncoding = testVideoEncoding;
        _openRecordingsFolder = openRecordingsFolder;
        _notifications = notifications;
        _savePlaybackAudioState = savePlaybackAudioState ?? ((_, _) => Task.FromResult(true));
        _playbackVolumePercent =
            initialStatus.EffectiveSettings?.Ui.PlaybackVolumePercent
            ?? UiSettings.DefaultPlaybackVolumePercent;
        _isPlaybackMuted =
            initialStatus.EffectiveSettings?.Ui.IsPlaybackMuted == true
            || _playbackVolumePercent == 0;
        _library = new RecordingLibraryViewModel(
            initialStatus.EffectiveSettings?.RecordingsDirectory,
            initialStatus.EffectiveSettings?.Ui.SelectedRecordingCategory
                ?? RecordingListCategory.ChallengeMode,
            loadRecordings,
            deleteRecording,
            setRecordingFavorite ?? ((_, _) => Task.CompletedTask),
            confirmPermanentDelete,
            saveSelectedRecordingCategory,
            message => CommandMessage = message,
            SetFavoriteFailureNotification
        );
        _library.PropertyChanged += OnRecordingLibraryPropertyChanged;
        ApplyStatus(initialStatus);
        _ = RefreshRecordingsAsync();
    }

    public ObservableCollection<RecordingCategoryTab> RecordingCategories =>
        _library.RecordingCategories;

    public ObservableCollection<RecordingListItem> Recordings => _library.Recordings;

    public string ActivityColumnHeader => _library.ActivityColumnHeader;

    public string PullNumberColumnHeader => _library.PullNumberColumnHeader;

    public string ContextColumnHeader => _library.ContextColumnHeader;

    public string ResultColumnHeader => _library.ResultColumnHeader;

    public string DurationColumnHeader => _library.DurationColumnHeader;

    public bool IsPullNumberColumnVisible => _library.IsPullNumberColumnVisible;

    public bool IsContextColumnVisible => _library.IsContextColumnVisible;

    public bool IsResultColumnVisible => _library.IsResultColumnVisible;

    public bool IsDurationColumnVisible => _library.IsDurationColumnVisible;

    public GridLength PullNumberColumnWidth => _library.PullNumberColumnWidth;

    public GridLength ContextColumnWidth => _library.ContextColumnWidth;

    public GridLength ResultColumnWidth => _library.ResultColumnWidth;

    public GridLength DurationColumnWidth => _library.DurationColumnWidth;

    public GridLength ScrollBarColumnWidth => new(SystemParameters.VerticalScrollBarWidth);

    public RecordingCategoryTab SelectedRecordingCategory
    {
        get => _library.SelectedRecordingCategory;
        set => _library.SelectedRecordingCategory = value;
    }

    public string StateTitle =>
        IsVideoEncodingSetupRequired ? "Setup needed" : GetStateTitle(_recording, _wowProcess);

    public string ReadinessDetail =>
        IsVideoEncodingSetupRequired
            ? GetVideoEncodingSetupRequiredDetail(_videoEncoding, _recording.LastFailure)
            : GetReadinessDetail(_recording, _combatLog, _wowProcess);

    public RecordingStatusHealth StatusHealth =>
        IsVideoEncodingSetupRequired
            ? RecordingStatusHealth.AttentionNeeded
            : GetStatusHealth(_recording, _combatLog, _wowProcess);

    public bool IsVideoEncodingSetupRequired =>
        _recording.State == RecordingCoordinatorState.Idle
        && (_videoEncoding?.IsValid != true || IsVideoEncodingSetupFailure(_recording.LastFailure));

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
        get => _library.SelectedRecording;
        set => _library.SelectedRecording = value;
    }

    public string RecordingLibraryStatus => _library.RecordingLibraryStatus;

    public bool IsPlayerPlaceholderVisible => SelectedRecording is null;

    public int PlaybackVolumePercent
    {
        get => _playbackVolumePercent;
        private set => SetProperty(ref _playbackVolumePercent, value);
    }

    public bool IsPlaybackMuted
    {
        get => _isPlaybackMuted;
        private set => SetProperty(ref _isPlaybackMuted, value);
    }

    public RecordingStatusHealth RecorderHealth =>
        _recording.LastFailure is not null
        && !IsTargetUnavailableFailure(_recording.LastFailure)
        && !IsVideoEncodingSetupFailure(_recording.LastFailure)
            ? RecordingStatusHealth.AttentionNeeded
        : _recording.State == RecordingCoordinatorState.Idle ? RecordingStatusHealth.Idle
        : RecordingStatusHealth.Active;

    public string? FailureMessage =>
        IsTargetUnavailableFailure(_recording.LastFailure)
        || IsVideoEncodingSetupFailure(_recording.LastFailure)
            ? null
            : _recording.LastFailure?.Message;

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

    public async Task UpdatePlaybackAudioStateAsync(int volumePercent, bool isMuted)
    {
        var normalizedVolumePercent = Math.Clamp(volumePercent, 0, 100);
        var normalizedIsMuted = isMuted || normalizedVolumePercent == 0;

        if (
            normalizedVolumePercent == PlaybackVolumePercent
            && normalizedIsMuted == IsPlaybackMuted
            && !_isPlaybackAudioSavePending
        )
        {
            return;
        }

        PlaybackVolumePercent = normalizedVolumePercent;
        IsPlaybackMuted = normalizedIsMuted;
        var saveVersion = ++_playbackAudioSaveVersion;

        try
        {
            var wasPersisted = await _savePlaybackAudioState(
                PlaybackVolumePercent,
                IsPlaybackMuted
            );

            if (saveVersion == _playbackAudioSaveVersion)
            {
                _isPlaybackAudioSavePending = !wasPersisted;
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The playback preference can safely remain in-memory during shutdown.
            if (saveVersion == _playbackAudioSaveVersion)
            {
                _isPlaybackAudioSavePending = true;
            }
        }
    }

    public bool IsFailureVisible =>
        _recording.LastFailure is not null
        && !IsTargetUnavailableFailure(_recording.LastFailure)
        && !IsVideoEncodingSetupFailure(_recording.LastFailure)
        && !ReferenceEquals(_recording.LastFailure, _dismissedFailure);

    public bool IsManualStopMode => _recording.State == RecordingCoordinatorState.Recording;

    public string ManualRecordingButtonText => IsManualStopMode ? "Manual stop" : "Manual start";

    public bool IsStatusCardStopMode => !IsVideoEncodingSetupRequired && IsManualStopMode;

    public string StatusCardButtonText =>
        IsVideoEncodingSetupRequired
            ? IsTestingVideoEncoding
                ? "Testing video encoding..."
                : "Test video encoding"
            : ManualRecordingButtonText;

    public bool IsTestingVideoEncoding
    {
        get => _isTestingVideoEncoding;
        private set
        {
            if (SetProperty(ref _isTestingVideoEncoding, value))
            {
                OnPropertyChanged(nameof(StatusCardButtonText));
                StatusCardCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CollapsedStatusToolTip =>
        IsVideoEncodingSetupRequired
            ? "Test video encoding before recording."
            : _recording.State switch
            {
                RecordingCoordinatorState.Recording => "Recording. Click to stop.",
                RecordingCoordinatorState.Idle when _wowProcess.IsWindowAvailable =>
                    "Idle. Click to start manual recording.",
                _ => ReadinessDetail,
            };

    public void ApplyStatus(ApplicationStatus status)
    {
        var previousSavedCount = _knownSavedCount;
        var previousActiveOutputPath = _recording.ActiveOutputPath;
        _recording = status.Recording;
        _combatLog = status.CombatLog;
        _wowProcess = status.WowProcess;
        _videoEncoding = status.VideoEncoding;
        var directoryChanged = _library.UpdateDirectory(
            status.EffectiveSettings?.RecordingsDirectory
        );
        _knownSavedCount = status.Recording.Statistics.SavedCount;

        if (CommandMessage == "The recording command failed." && _recording.LastFailure is not null)
        {
            CommandMessage = GetFailureCommandMessage(_recording.LastFailure);
        }

        UpdateDuration();
        if (directoryChanged || previousSavedCount != _knownSavedCount)
        {
            var savedRecordingCompleted = _knownSavedCount > previousSavedCount;
            _ = RefreshRecordingsAsync(
                savedRecordingCompleted ? previousActiveOutputPath : null,
                savedRecordingCompleted
            );
        }

        OnPropertyChanged(string.Empty);
        UpdateFailureNotification();
        ManualRecordingCommand.NotifyCanExecuteChanged();
        StatusCardCommand.NotifyCanExecuteChanged();
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
        || (
            _recording.State == RecordingCoordinatorState.Idle
            && _wowProcess.IsWindowAvailable
            && !IsVideoEncodingSetupRequired
        );

    private bool CanRunStatusCardCommand =>
        IsVideoEncodingSetupRequired
            ? _recording.State == RecordingCoordinatorState.Idle && !IsTestingVideoEncoding
            : CanRunManualCommand;

    private bool CanDeleteSelectedRecording => _library.SelectedRecording is not null;

    internal Task RefreshRecordingsAsync(
        string? preferredSelectionPath = null,
        bool preferMostRecent = false
    )
    {
        return _library.RefreshAsync(preferredSelectionPath, preferMostRecent);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRecording))]
    private Task DeleteSelectedRecordingAsync()
    {
        return ExecuteCommandAsync(_library.DeleteSelectedRecordingAsync);
    }

    [RelayCommand]
    private Task ToggleFavoriteAsync(RecordingListItem recording)
    {
        return _library.ToggleFavoriteAsync(recording);
    }

    private void OnRecordingLibraryPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(args.PropertyName);

        if (args.PropertyName == nameof(SelectedRecording))
        {
            OnPropertyChanged(nameof(IsPlayerPlaceholderVisible));
            DeleteSelectedRecordingCommand.NotifyCanExecuteChanged();
        }
    }

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

    [RelayCommand(CanExecute = nameof(CanRunStatusCardCommand))]
    private Task StatusCardAsync()
    {
        return IsVideoEncodingSetupRequired
            ? TestVideoEncodingAsync()
            : ExecuteCommandAsync(ToggleManualRecordingAsync);
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

    private async Task TestVideoEncodingAsync()
    {
        IsTestingVideoEncoding = true;
        try
        {
            await _testVideoEncoding();
            CommandMessage = null;
        }
        catch (Exception exception)
        {
            CommandMessage = $"Video encoding test failed: {exception.Message}";
        }
        finally
        {
            IsTestingVideoEncoding = false;
        }
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
        UpdateFailureNotification();
        DismissFailureCommand.NotifyCanExecuteChanged();
    }

    private void UpdateFailureNotification()
    {
        if (!IsFailureVisible || string.IsNullOrWhiteSpace(FailureMessage))
        {
            _notifications?.Dismiss(FailureNotificationId);
            return;
        }

        _notifications?.ShowOrUpdate(
            FailureNotificationId,
            new NotificationContent(
                NotificationSeverity.Error,
                "Recorder needs attention",
                FailureMessage,
                Dismissed: DismissFailure
            )
        );
    }

    private void SetFavoriteFailureNotification(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _notifications?.Dismiss(FavoriteFailureNotificationId);
            return;
        }

        _notifications?.ShowOrUpdate(
            FavoriteFailureNotificationId,
            new NotificationContent(NotificationSeverity.Error, "Favourite update failed", message)
        );
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

    private static string GetVideoEncodingSetupRequiredDetail(
        EncoderCalibrationStatus? videoEncoding,
        Exception? failure
    )
    {
        var message =
            TryGetVideoEncodingSetupFailureMessage(failure)
            ?? videoEncoding?.Message
            ?? VideoEncodingSetupFallbackMessage;

        return JoinSentences(message, VideoEncodingSetupRequiredSuffix);
    }

    private static bool IsVideoEncodingSetupFailure(Exception? exception)
    {
        return VideoEncodingSetupFailureClassifier.IsSetupFailure(exception);
    }

    private static string? TryGetVideoEncodingSetupFailureMessage(Exception? exception)
    {
        return VideoEncodingSetupFailureClassifier.TryGetMessage(exception);
    }
}
