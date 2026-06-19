using System.Globalization;
using System.Runtime.CompilerServices;

namespace PullWatch;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly Func<
        PullWatchSettings,
        CancellationToken,
        Task<SettingsSaveResult>
    > _saveSettings;
    private readonly ISettingsDialogs _dialogs;
    private PullWatchSettings _savedSettings;
    private bool _isLoading;
    private bool _isEditingEnabled;
    private bool _isDirty;
    private string? _wowLogsDirectory;
    private string? _recordingsDirectory;
    private bool _recordMythicPlus;
    private bool _recordRaidEncounters;
    private string _bitrateMegabits = string.Empty;
    private string _frameRate = string.Empty;
    private bool _captureSystemAudio;
    private bool _captureMicrophone;
    private bool _captureCursor;
    private bool _showCaptureBorder;
    private string? _wowLogsDirectoryError;
    private string? _recordingsDirectoryError;
    private string? _bitrateError;
    private string? _frameRateError;
    private string? _saveMessage;
    private bool _isSaveError;

    public SettingsViewModel(
        ApplicationStatus initialStatus,
        Func<PullWatchSettings, CancellationToken, Task<SettingsSaveResult>> saveSettings,
        ISettingsDialogs dialogs
    )
    {
        _saveSettings = saveSettings;
        _dialogs = dialogs;
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

    public string? WowLogsDirectory
    {
        get => _wowLogsDirectory;
        set => SetEditableProperty(ref _wowLogsDirectory, value);
    }

    public string? RecordingsDirectory
    {
        get => _recordingsDirectory;
        set => SetEditableProperty(ref _recordingsDirectory, value);
    }

    public bool RecordMythicPlus
    {
        get => _recordMythicPlus;
        set => SetEditableProperty(ref _recordMythicPlus, value);
    }

    public bool RecordRaidEncounters
    {
        get => _recordRaidEncounters;
        set => SetEditableProperty(ref _recordRaidEncounters, value);
    }

    public string BitrateMegabits
    {
        get => _bitrateMegabits;
        set => SetEditableProperty(ref _bitrateMegabits, value);
    }

    public string FrameRate
    {
        get => _frameRate;
        set => SetEditableProperty(ref _frameRate, value);
    }

    public bool CaptureSystemAudio
    {
        get => _captureSystemAudio;
        set => SetEditableProperty(ref _captureSystemAudio, value);
    }

    public bool CaptureMicrophone
    {
        get => _captureMicrophone;
        set => SetEditableProperty(ref _captureMicrophone, value);
    }

    public bool CaptureCursor
    {
        get => _captureCursor;
        set => SetEditableProperty(ref _captureCursor, value);
    }

    public bool ShowCaptureBorder
    {
        get => _showCaptureBorder;
        set => SetEditableProperty(ref _showCaptureBorder, value);
    }

    public bool IsEditingEnabled
    {
        get => _isEditingEnabled;
        private set
        {
            if (SetProperty(ref _isEditingEnabled, value))
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
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
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
        get => _wowLogsDirectoryError;
        private set => SetProperty(ref _wowLogsDirectoryError, value);
    }

    public string? RecordingsDirectoryError
    {
        get => _recordingsDirectoryError;
        private set => SetProperty(ref _recordingsDirectoryError, value);
    }

    public string? BitrateError
    {
        get => _bitrateError;
        private set => SetProperty(ref _bitrateError, value);
    }

    public string? FrameRateError
    {
        get => _frameRateError;
        private set => SetProperty(ref _frameRateError, value);
    }

    public string? SaveMessage
    {
        get => _saveMessage;
        private set => SetProperty(ref _saveMessage, value);
    }

    public bool IsSaveError
    {
        get => _isSaveError;
        private set => SetProperty(ref _isSaveError, value);
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
        if (
            !decimal.TryParse(
                BitrateMegabits,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out var bitrateMegabits
            )
            || bitrateMegabits <= 0
            || bitrateMegabits * 1_000_000m > int.MaxValue
            || decimal.Truncate(bitrateMegabits * 1_000_000m) != bitrateMegabits * 1_000_000m
        )
        {
            BitrateError = "Enter a valid bitrate in Mbps.";
        }

        if (
            !int.TryParse(
                FrameRate,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out var frameRate
            )
        )
        {
            FrameRateError = "Enter a valid whole-number frame rate.";
        }

        if (BitrateError is not null || FrameRateError is not null)
        {
            return null;
        }

        return _savedSettings with
        {
            WowLogsDirectory = NormalizeEmpty(WowLogsDirectory),
            RecordingsDirectory = NormalizeEmpty(RecordingsDirectory),
            RecordMythicPlus = RecordMythicPlus,
            RecordRaidEncounters = RecordRaidEncounters,
            Video = _savedSettings.Video with
            {
                Bitrate = (int)(bitrateMegabits * 1_000_000m),
                FrameRate = frameRate,
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
            else if (error.StartsWith("Video bitrate", StringComparison.Ordinal))
            {
                BitrateError = error;
            }
            else if (error.StartsWith("Video frame rate", StringComparison.Ordinal))
            {
                FrameRateError = error;
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
            BitrateMegabits = (settings.Video.Bitrate / 1_000_000m).ToString(
                "0.###",
                CultureInfo.CurrentCulture
            );
            FrameRate = settings.Video.FrameRate.ToString(CultureInfo.CurrentCulture);
            CaptureSystemAudio = settings.Audio.CaptureSystemAudio;
            CaptureMicrophone = settings.Audio.CaptureMicrophone;
            CaptureCursor = settings.Video.CaptureCursor;
            ShowCaptureBorder = settings.Video.ShowCaptureBorder;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ClearErrors()
    {
        WowLogsDirectoryError = null;
        RecordingsDirectoryError = null;
        BitrateError = null;
        FrameRateError = null;
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
    }

    private static string? NormalizeEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
