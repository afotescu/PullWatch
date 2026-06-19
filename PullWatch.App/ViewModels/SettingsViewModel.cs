using System.Runtime.CompilerServices;
using Forms = System.Windows.Forms;

namespace PullWatch;

public sealed class SettingsViewModel : ObservableObject
{
    private static readonly VideoCaptureSize FallbackEstimateCaptureSize = new(1920, 1080);
    private static readonly TimeSpan EstimateDuration = TimeSpan.FromMinutes(5);

    private readonly Func<
        PullWatchSettings,
        CancellationToken,
        Task<SettingsSaveResult>
    > _saveSettings;
    private readonly ISettingsDialogs _dialogs;
    private readonly Func<VideoCaptureSize> _getEstimateCaptureSize;
    private PullWatchSettings _savedSettings;
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
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave, HandleCommandFailure);
        PickWowLogsDirectoryCommand = new RelayCommand(
            PickWowLogsDirectory,
            () => IsEditingEnabled
        );
        PickRecordingsDirectoryCommand = new RelayCommand(
            PickRecordingsDirectory,
            () => IsEditingEnabled
        );
        LoadSettings(_savedSettings);
        ApplyStatus(initialStatus);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand PickWowLogsDirectoryCommand { get; }

    public RelayCommand PickRecordingsDirectoryCommand { get; }

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
                OnPropertyChanged(nameof(CanSave));
                SaveCommand.NotifyCanExecuteChanged();
                PickWowLogsDirectoryCommand.NotifyCanExecuteChanged();
                PickRecordingsDirectoryCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsDirty
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanSave));
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanSave => IsEditingEnabled && IsDirty;

    public string EditingStatus =>
        IsEditingEnabled
            ? "Settings are available while PullWatch is idle."
            : "Settings are locked until the active recording finishes.";

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

        if (
            !IsDirty
            && status.EffectiveSettings is not null
            && status.EffectiveSettings != _savedSettings
        )
        {
            _savedSettings = status.EffectiveSettings;
            LoadSettings(_savedSettings);
        }
    }

    public async Task<bool> SaveChangesAsync()
    {
        if (!CanSave)
        {
            return !IsDirty;
        }

        ClearErrors();
        var settings = BuildSettings();

        if (settings is null)
        {
            return false;
        }

        var result = await _saveSettings(settings, CancellationToken.None);

        if (!result.IsSaved)
        {
            ApplySaveFailure(result);
            return false;
        }

        _savedSettings = result.Settings!;
        LoadSettings(_savedSettings);
        IsDirty = false;
        IsSaveError = result.Status == SettingsSaveStatus.ApplicationFailed;
        SaveMessage =
            result.Status == SettingsSaveStatus.ApplicationFailed
                ? $"Settings were saved, but combat-log monitoring could not restart: {result.Error?.Message}"
                : "Settings saved and active.";
        return true;
    }

    public void DiscardChanges()
    {
        LoadSettings(_savedSettings);
        ClearErrors();
        SaveMessage = null;
        IsSaveError = false;
        IsDirty = false;
    }

    private Task SaveAsync()
    {
        return SaveChangesAsync();
    }

    private void PickWowLogsDirectory()
    {
        var selected = _dialogs.PickFolder(
            "Select the World of Warcraft logs directory",
            WowLogsDirectory
        );

        if (selected is not null)
        {
            WowLogsDirectory = selected;
        }
    }

    private void PickRecordingsDirectory()
    {
        var selected = _dialogs.PickFolder("Select the recordings directory", RecordingsDirectory);

        if (selected is not null)
        {
            RecordingsDirectory = selected;
        }
    }

    private PullWatchSettings? BuildSettings()
    {
        return _savedSettings with
        {
            WowLogsDirectory = NormalizeEmpty(WowLogsDirectory),
            RecordingsDirectory = NormalizeEmpty(RecordingsDirectory),
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

    private void ApplySaveFailure(SettingsSaveResult result)
    {
        IsSaveError = true;

        if (result.Status == SettingsSaveStatus.RecordingActive)
        {
            SaveMessage = "Settings cannot be saved while a recording is active.";
            return;
        }

        if (result.Status == SettingsSaveStatus.PersistenceFailed)
        {
            SaveMessage = $"Could not save settings: {result.Error?.Message}";
            return;
        }

        SaveMessage = "Fix the highlighted settings before saving.";

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
        IsSaveError = true;
        SaveMessage = $"Could not save settings: {exception.Message}";
    }

    private void LoadSettings(PullWatchSettings settings)
    {
        _isLoading = true;

        try
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
        }
        finally
        {
            _isLoading = false;
        }

        OnPropertyChanged(nameof(EstimatedRecordingSize));
    }

    private void ClearErrors()
    {
        WowLogsDirectoryError = null;
        RecordingsDirectoryError = null;
        SaveMessage = null;
        IsSaveError = false;
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
            IsDirty = true;
            SaveMessage = null;
        }

        if (ShouldRefreshEstimate(propertyName))
        {
            OnPropertyChanged(nameof(EstimatedRecordingSize));
        }
    }

    private static string? NormalizeEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
