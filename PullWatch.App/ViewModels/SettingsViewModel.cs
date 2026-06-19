using System.IO;
using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;

namespace PullWatch;

public sealed class SettingsViewModel : ObservableObject
{
    private const string SettingsSavedMessage = "Settings saved.";
    private const string FixHighlightedSettingsMessage = "Fix the highlighted settings.";
    private const string SettingsLockedMessage = "Settings are locked while recording.";

    private static readonly VideoCaptureSize FallbackEstimateCaptureSize = new(1920, 1080);
    private static readonly TimeSpan EstimateDuration = TimeSpan.FromMinutes(5);

    private readonly Func<
        PullWatchSettings,
        CancellationToken,
        Task<SettingsSaveResult>
    > _saveSettings;
    private readonly ISettingsDialogs _dialogs;
    private readonly Func<VideoCaptureSize> _getEstimateCaptureSize;
    private readonly object _autosaveSync = new();
    private PullWatchSettings _savedSettings;
    private Task? _autosaveTask;
    private bool _hasQueuedAutosave;
    private bool _queuedSaveIncludesWowLogsDirectory;
    private bool _queuedSaveIncludesRecordingsDirectory;
    private bool _retryWowLogsDirectoryOnNextAutosave;
    private bool _retryRecordingsDirectoryOnNextAutosave;
    private bool _isLoading;

    public SettingsViewModel(
        ApplicationStatus initialStatus,
        Func<PullWatchSettings, CancellationToken, Task<SettingsSaveResult>> saveSettings,
        ISettingsDialogs dialogs,
        Func<VideoCaptureSize>? getEstimateCaptureSize = null
    )
    {
        _saveSettings = saveSettings;
        _dialogs = dialogs;
        _getEstimateCaptureSize = getEstimateCaptureSize ?? GetPrimaryDisplayCaptureSize;
        _savedSettings = initialStatus.EffectiveSettings ?? new PullWatchSettings();
        CommitWowLogsDirectoryCommand = new AsyncRelayCommand(
            CommitWowLogsDirectoryAsync,
            () => IsEditingEnabled,
            HandleCommandFailure
        );
        CommitRecordingsDirectoryCommand = new AsyncRelayCommand(
            CommitRecordingsDirectoryAsync,
            () => IsEditingEnabled,
            HandleCommandFailure
        );
        PickWowLogsDirectoryCommand = new AsyncRelayCommand(
            PickWowLogsDirectoryAsync,
            () => IsEditingEnabled,
            HandleCommandFailure
        );
        PickRecordingsDirectoryCommand = new AsyncRelayCommand(
            PickRecordingsDirectoryAsync,
            () => IsEditingEnabled,
            HandleCommandFailure
        );
        LoadSettings(_savedSettings);
        ApplyStatus(initialStatus);
    }

    public AsyncRelayCommand CommitWowLogsDirectoryCommand { get; }

    public AsyncRelayCommand CommitRecordingsDirectoryCommand { get; }

    public AsyncRelayCommand PickWowLogsDirectoryCommand { get; }

    public AsyncRelayCommand PickRecordingsDirectoryCommand { get; }

    public IReadOnlyList<VideoQualityOption> VideoQualityOptions { get; } =
    [
        new(VideoQuality.Compact, "Compact"),
        new(VideoQuality.Balanced, "Balanced"),
        new(VideoQuality.High, "High"),
    ];

    public IReadOnlyList<FrameRateOption> FrameRateOptions { get; } =
        VideoFrameRates
            .Supported.Select(frameRate => new FrameRateOption(frameRate, $"{frameRate} FPS"))
            .ToArray();

    public string? WowLogsDirectory
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public string? RecordingsDirectory
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool RecordMythicPlus
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool RecordRaidEncounters
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public VideoQuality SelectedVideoQuality
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public int SelectedFrameRate
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool CaptureSystemAudio
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool CaptureMicrophone
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool CaptureCursor
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool ShowCaptureBorder
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool IsEditingEnabled
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                NotifyCommandStatesChanged();
            }
        }
    }

    public string EditingStatus =>
        IsEditingEnabled
            ? "Settings are available while PullWatch is idle."
            : "Settings are locked until the active recording finishes.";

    public bool IsWowLogsDirectoryPending
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsRecordingsDirectoryPending
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string? WowLogsDirectoryError
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string? RecordingsDirectoryError
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string? SaveMessage
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsSaveError
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string EstimatedRecordingSize
    {
        get
        {
            var captureSize = _getEstimateCaptureSize();
            var bitrate = VideoBitrateCalculator.CalculateBitrate(
                captureSize,
                SelectedFrameRate,
                SelectedVideoQuality
            );
            var megabytes = VideoBitrateCalculator.EstimateFileSizeMegabytes(
                bitrate,
                new AudioSettings
                {
                    CaptureSystemAudio = CaptureSystemAudio,
                    CaptureMicrophone = CaptureMicrophone,
                },
                EstimateDuration
            );

            return string.Join(
                " ",
                $"About {FormatFileSize(megabytes)} per 5 minutes",
                $"at full-screen {captureSize.Width}x{captureSize.Height},",
                $"{SelectedFrameRate} FPS",
                $"({VideoBitrateCalculator.ToMegabitsPerSecond(bitrate)} Mbps target)."
            );
        }
    }

    public void ApplyStatus(ApplicationStatus status)
    {
        IsEditingEnabled = status.Recording.State == RecordingCoordinatorState.Idle;
        OnPropertyChanged(nameof(EditingStatus));

        if (!IsEditingEnabled)
        {
            SaveMessage = SettingsLockedMessage;
            IsSaveError = false;
            return;
        }

        if (SaveMessage == SettingsLockedMessage)
        {
            RestoreUnlockedStatus();
        }

        if (
            status.EffectiveSettings is not null
            && status.EffectiveSettings != _savedSettings
            && !HasPendingLocalChanges()
        )
        {
            _savedSettings = status.EffectiveSettings;
            LoadSettings(_savedSettings);
        }
    }

    public Task<bool> CommitWowLogsDirectoryAsync()
    {
        return CommitPathAsync(includeWowLogsDirectory: true, includeRecordingsDirectory: false);
    }

    public Task<bool> CommitRecordingsDirectoryAsync()
    {
        return CommitPathAsync(includeWowLogsDirectory: false, includeRecordingsDirectory: true);
    }

    public void DiscardChanges()
    {
        IsWowLogsDirectoryPending = false;
        IsRecordingsDirectoryPending = false;
        _retryWowLogsDirectoryOnNextAutosave = false;
        _retryRecordingsDirectoryOnNextAutosave = false;
        LoadSettings(_savedSettings);
        ClearErrors();
        SaveMessage = null;
        IsSaveError = false;
    }

    private async Task PickWowLogsDirectoryAsync()
    {
        var selected = _dialogs.PickFolder(
            "Select the World of Warcraft logs directory",
            WowLogsDirectory
        );

        if (selected is not null)
        {
            WowLogsDirectory = selected;
            await CommitWowLogsDirectoryAsync();
        }
    }

    private async Task PickRecordingsDirectoryAsync()
    {
        var selected = _dialogs.PickFolder("Select the recordings directory", RecordingsDirectory);

        if (selected is not null)
        {
            RecordingsDirectory = selected;
            await CommitRecordingsDirectoryAsync();
        }
    }

    private async Task<bool> CommitPathAsync(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        if (!IsEditingEnabled)
        {
            ApplyLockedStatus();
            return false;
        }

        var hasPendingIncludedPath =
            (includeWowLogsDirectory && IsWowLogsDirectoryPending)
            || (includeRecordingsDirectory && IsRecordingsDirectoryPending);

        if (!hasPendingIncludedPath)
        {
            return true;
        }

        await QueueAutosaveAsync(includeWowLogsDirectory, includeRecordingsDirectory);
        return !IsSaveError;
    }

    private PullWatchSettings BuildSettings(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        return _savedSettings with
        {
            WowLogsDirectory = includeWowLogsDirectory
                ? NormalizeEmpty(WowLogsDirectory)
                : _savedSettings.WowLogsDirectory,
            RecordingsDirectory = includeRecordingsDirectory
                ? NormalizeEmpty(RecordingsDirectory)
                : _savedSettings.RecordingsDirectory,
            RecordMythicPlus = RecordMythicPlus,
            RecordRaidEncounters = RecordRaidEncounters,
            Video = _savedSettings.Video with
            {
                Quality = SelectedVideoQuality,
                FrameRate = SelectedFrameRate,
                CaptureCursor = CaptureCursor,
                ShowCaptureBorder = ShowCaptureBorder,
            },
            Audio = _savedSettings.Audio with
            {
                CaptureSystemAudio = CaptureSystemAudio,
                CaptureMicrophone = CaptureMicrophone,
            },
        };
    }

    private Task QueueAutosaveAsync(bool includeWowLogsDirectory, bool includeRecordingsDirectory)
    {
        lock (_autosaveSync)
        {
            _hasQueuedAutosave = true;
            _queuedSaveIncludesWowLogsDirectory |=
                includeWowLogsDirectory || _retryWowLogsDirectoryOnNextAutosave;
            _queuedSaveIncludesRecordingsDirectory |=
                includeRecordingsDirectory || _retryRecordingsDirectoryOnNextAutosave;
            _autosaveTask ??= ProcessAutosavesAsync();

            if (_autosaveTask.IsCompleted)
            {
                _autosaveTask = ProcessAutosavesAsync();
            }

            return _autosaveTask;
        }
    }

    private async Task ProcessAutosavesAsync()
    {
        await Task.Yield();

        while (true)
        {
            bool includeWowLogsDirectory;
            bool includeRecordingsDirectory;

            lock (_autosaveSync)
            {
                if (!_hasQueuedAutosave)
                {
                    _autosaveTask = null;
                    return;
                }

                _hasQueuedAutosave = false;
                includeWowLogsDirectory = _queuedSaveIncludesWowLogsDirectory;
                includeRecordingsDirectory = _queuedSaveIncludesRecordingsDirectory;
                _queuedSaveIncludesWowLogsDirectory = false;
                _queuedSaveIncludesRecordingsDirectory = false;
            }

            await RunAutosaveAsync(includeWowLogsDirectory, includeRecordingsDirectory);
        }
    }

    private async Task RunAutosaveAsync(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        if (!IsEditingEnabled)
        {
            ApplyLockedStatus();
            return;
        }

        var settings = BuildSettings(includeWowLogsDirectory, includeRecordingsDirectory);

        try
        {
            var result = await _saveSettings(settings, CancellationToken.None);

            if (!result.IsSaved)
            {
                ApplySaveFailure(
                    result,
                    includeWowLogsDirectory,
                    includeRecordingsDirectory,
                    settings
                );
                return;
            }

            ApplySaveSuccess(result, includeWowLogsDirectory, includeRecordingsDirectory);
        }
        catch (Exception exception)
        {
            HandleAutosaveException(
                exception,
                includeWowLogsDirectory,
                includeRecordingsDirectory,
                settings
            );
        }
    }

    private void ApplySaveSuccess(
        SettingsSaveResult result,
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        var savedSettings = result.Settings!;
        _savedSettings = savedSettings;

        if (
            includeWowLogsDirectory
            && PathsMatchSaved(WowLogsDirectory, savedSettings.WowLogsDirectory)
        )
        {
            IsWowLogsDirectoryPending = false;
            _retryWowLogsDirectoryOnNextAutosave = false;
            SetLoadedValue(() => WowLogsDirectory = savedSettings.WowLogsDirectory);
            WowLogsDirectoryError = null;
        }

        if (
            includeRecordingsDirectory
            && PathsMatchSaved(RecordingsDirectory, savedSettings.RecordingsDirectory)
        )
        {
            IsRecordingsDirectoryPending = false;
            _retryRecordingsDirectoryOnNextAutosave = false;
            SetLoadedValue(() => RecordingsDirectory = savedSettings.RecordingsDirectory);
            RecordingsDirectoryError = null;
        }

        if (HasPathErrors())
        {
            SaveMessage = FixHighlightedSettingsMessage;
            IsSaveError = true;
            return;
        }

        IsSaveError = result.Status == SettingsSaveStatus.ApplicationFailed;

        if (result.Status == SettingsSaveStatus.ApplicationFailed)
        {
            SaveMessage =
                $"Settings saved, but combat-log monitoring could not restart: {result.Error?.Message}";
            return;
        }

        SaveMessage =
            IsWowLogsDirectoryPending || IsRecordingsDirectoryPending ? null : SettingsSavedMessage;
    }

    private void ApplySaveFailure(
        SettingsSaveResult result,
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory,
        PullWatchSettings attemptedSettings
    )
    {
        if (result.Status == SettingsSaveStatus.RecordingActive)
        {
            ApplyLockedStatus();
            return;
        }

        IsSaveError = true;

        if (result.Status == SettingsSaveStatus.PersistenceFailed)
        {
            MarkIncludedPathsForRetry(
                includeWowLogsDirectory,
                includeRecordingsDirectory,
                attemptedSettings
            );
            SaveMessage = $"Could not save settings: {result.Error?.Message}";
            return;
        }

        ClearIncludedPathRetries(includeWowLogsDirectory, includeRecordingsDirectory);
        SaveMessage = FixHighlightedSettingsMessage;

        foreach (var error in result.ValidationErrors)
        {
            if (error.StartsWith("WoW logs directory", StringComparison.Ordinal))
            {
                WowLogsDirectoryError = error;
            }
            else if (error.StartsWith("Recordings directory", StringComparison.Ordinal))
            {
                RecordingsDirectoryError = error;
            }
        }
    }

    private void HandleCommandFailure(Exception exception)
    {
        HandleAutosaveException(
            exception,
            includeWowLogsDirectory: false,
            includeRecordingsDirectory: false,
            attemptedSettings: null
        );
    }

    private void HandleAutosaveException(
        Exception exception,
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory,
        PullWatchSettings? attemptedSettings
    )
    {
        IsSaveError = true;
        MarkIncludedPathsForRetry(
            includeWowLogsDirectory,
            includeRecordingsDirectory,
            attemptedSettings
        );
        SaveMessage = $"Could not save settings: {exception.Message}";
    }

    private void LoadSettings(PullWatchSettings settings)
    {
        SetLoadedValue(() =>
        {
            WowLogsDirectory = settings.WowLogsDirectory;
            RecordingsDirectory = settings.RecordingsDirectory;
            RecordMythicPlus = settings.RecordMythicPlus;
            RecordRaidEncounters = settings.RecordRaidEncounters;
            SelectedVideoQuality = settings.Video.Quality;
            SelectedFrameRate = settings.Video.FrameRate;
            CaptureSystemAudio = settings.Audio.CaptureSystemAudio;
            CaptureMicrophone = settings.Audio.CaptureMicrophone;
            CaptureCursor = settings.Video.CaptureCursor;
            ShowCaptureBorder = settings.Video.ShowCaptureBorder;
        });

        OnPropertyChanged(nameof(EstimatedRecordingSize));
    }

    private void ClearErrors()
    {
        WowLogsDirectoryError = null;
        RecordingsDirectoryError = null;
    }

    private void SetEditableProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null
    )
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        if (!_isLoading)
        {
            ClearTransientSuccessStatus();

            if (!IsEditingEnabled)
            {
                ApplyLockedStatus();
            }
            else if (MarkPendingPath(propertyName))
            {
                // Path text boxes save only after an explicit commit.
            }
            else
            {
                _ = QueueAutosaveAsync(
                    includeWowLogsDirectory: false,
                    includeRecordingsDirectory: false
                );
            }
        }

        if (ShouldRefreshEstimate(propertyName))
        {
            OnPropertyChanged(nameof(EstimatedRecordingSize));
        }
    }

    private bool MarkPendingPath(string? propertyName)
    {
        if (propertyName == nameof(WowLogsDirectory))
        {
            IsWowLogsDirectoryPending = true;
            _retryWowLogsDirectoryOnNextAutosave = false;
            return true;
        }

        if (propertyName == nameof(RecordingsDirectory))
        {
            IsRecordingsDirectoryPending = true;
            _retryRecordingsDirectoryOnNextAutosave = false;
            return true;
        }

        return false;
    }

    private void SetLoadedValue(Action update)
    {
        _isLoading = true;

        try
        {
            update();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ClearTransientSuccessStatus()
    {
        if (!IsSaveError && SaveMessage == SettingsSavedMessage)
        {
            SaveMessage = null;
        }
    }

    private void ApplyLockedStatus()
    {
        SaveMessage = SettingsLockedMessage;
        IsSaveError = false;
    }

    private void RestoreUnlockedStatus()
    {
        if (HasPathErrors())
        {
            SaveMessage = FixHighlightedSettingsMessage;
            IsSaveError = true;
            return;
        }

        SaveMessage = null;
        IsSaveError = false;
    }

    private bool HasPendingLocalChanges()
    {
        lock (_autosaveSync)
        {
            return IsWowLogsDirectoryPending
                || IsRecordingsDirectoryPending
                || _hasQueuedAutosave
                || _autosaveTask is { IsCompleted: false };
        }
    }

    private void NotifyCommandStatesChanged()
    {
        CommitWowLogsDirectoryCommand.NotifyCanExecuteChanged();
        CommitRecordingsDirectoryCommand.NotifyCanExecuteChanged();
        PickWowLogsDirectoryCommand.NotifyCanExecuteChanged();
        PickRecordingsDirectoryCommand.NotifyCanExecuteChanged();
    }

    private void MarkIncludedPathsForRetry(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory,
        PullWatchSettings? attemptedSettings
    )
    {
        if (
            includeWowLogsDirectory
            && IsWowLogsDirectoryPending
            && attemptedSettings is not null
            && PathsMatchSaved(WowLogsDirectory, attemptedSettings.WowLogsDirectory)
        )
        {
            _retryWowLogsDirectoryOnNextAutosave = true;
        }

        if (
            includeRecordingsDirectory
            && IsRecordingsDirectoryPending
            && attemptedSettings is not null
            && PathsMatchSaved(RecordingsDirectory, attemptedSettings.RecordingsDirectory)
        )
        {
            _retryRecordingsDirectoryOnNextAutosave = true;
        }
    }

    private void ClearIncludedPathRetries(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        if (includeWowLogsDirectory)
        {
            _retryWowLogsDirectoryOnNextAutosave = false;
        }

        if (includeRecordingsDirectory)
        {
            _retryRecordingsDirectoryOnNextAutosave = false;
        }
    }

    private static string? NormalizeEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private bool HasPathErrors()
    {
        return WowLogsDirectoryError is not null || RecordingsDirectoryError is not null;
    }

    private static bool PathsMatchSaved(string? currentValue, string? savedValue)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(TryNormalizePath(currentValue), savedValue);
    }

    private static string? TryNormalizePath(string? value)
    {
        var normalized = NormalizeEmpty(value);

        if (normalized is null)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(normalized));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return normalized;
        }
    }

    private static bool ShouldRefreshEstimate(string? propertyName)
    {
        return propertyName
            is nameof(SelectedVideoQuality)
                or nameof(SelectedFrameRate)
                or nameof(CaptureSystemAudio)
                or nameof(CaptureMicrophone);
    }

    private static string FormatFileSize(int megabytes)
    {
        if (megabytes >= 1_000)
        {
            return $"{megabytes / 1_000d:0.#} GB";
        }

        var roundedMegabytes = Math.Max(1, (int)Math.Round(megabytes / 10d) * 10);
        return $"{roundedMegabytes} MB";
    }

    private static VideoCaptureSize GetPrimaryDisplayCaptureSize()
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds;

        return bounds is { Width: > 0, Height: > 0 }
            ? new VideoCaptureSize(bounds.Value.Width, bounds.Value.Height)
            : FallbackEstimateCaptureSize;
    }
}

public sealed record VideoQualityOption(VideoQuality Value, string Label);

public sealed record FrameRateOption(int Value, string Label);
