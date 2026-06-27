using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace PullWatch;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string SettingsSavedMessage = "Settings saved.";
    private const string FixHighlightedSettingsMessage = "Fix the highlighted settings.";
    private const string SettingsLockedMessage = "Settings are locked while recording.";

    private const long BytesPerKilobyte = 1024;
    private const long BytesPerMegabyte = BytesPerKilobyte * 1024;
    private const long BytesPerGigabyte = BytesPerMegabyte * 1024;
    private const int DefaultRecordingStorageLimitGigabytes = (int)(
        RecordingStorageSettings.DefaultMaxUsageBytes / BytesPerGigabyte
    );
    private const int MaximumRecordingStorageLimitGigabytes = 10_000;

    private static readonly VideoCaptureSize FallbackEstimateCaptureSize = new(1920, 1080);
    private static readonly TimeSpan EstimateDuration = TimeSpan.FromMinutes(1);

    private readonly Func<PullWatchSettings, Task<SettingsSaveResult>> _saveSettings;
    private readonly ISettingsDialogs _dialogs;
    private readonly Func<VideoCaptureSize> _getEstimateCaptureSize;
    private readonly IWindowsStartupShortcut _windowsStartupShortcut;
    private readonly object _autosaveSync = new();
    private PullWatchSettings _savedSettings;
    private Task? _autosaveTask;
    private PendingSettingsSave? _pendingAutosave;
    private PendingSettingsSave _retryOnNextAutosave = PendingSettingsSave.None;
    private RecordingStorageStatus _recordingStorageStatus = RecordingStorageStatus.Initial;
    private bool _isLoading;

    public SettingsViewModel(
        ApplicationStatus initialStatus,
        Func<PullWatchSettings, Task<SettingsSaveResult>> saveSettings,
        ISettingsDialogs dialogs,
        Func<VideoCaptureSize>? getEstimateCaptureSize = null,
        IWindowsStartupShortcut? windowsStartupShortcut = null,
        RecordingStorageStatus? initialRecordingStorageStatus = null
    )
    {
        _saveSettings = saveSettings;
        _dialogs = dialogs;
        _getEstimateCaptureSize = getEstimateCaptureSize ?? GetEstimatedCaptureSize;
        _windowsStartupShortcut = windowsStartupShortcut ?? NoOpWindowsStartupShortcut.Instance;
        _recordingStorageStatus = initialRecordingStorageStatus ?? RecordingStorageStatus.Initial;
        _savedSettings = initialStatus.EffectiveSettings ?? new PullWatchSettings();
        CommitWowLogsDirectoryCommand = new AsyncRelayCommand(
            () => ExecuteCommandAsync(CommitWowLogsDirectoryAsync),
            () => IsEditingEnabled
        );
        CommitRecordingsDirectoryCommand = new AsyncRelayCommand(
            () => ExecuteCommandAsync(CommitRecordingsDirectoryAsync),
            () => IsEditingEnabled
        );
        LoadSettings(_savedSettings);
        ApplyStatus(initialStatus);
    }

    public IAsyncRelayCommand CommitWowLogsDirectoryCommand { get; }

    public IAsyncRelayCommand CommitRecordingsDirectoryCommand { get; }

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

    public IReadOnlyList<VideoScalingOption> VideoScalingOptions
    {
        get
        {
            var captureSize = _getEstimateCaptureSize();
            var candidates = new (VideoScaling Value, string Label, VideoCaptureSize OutputSize)[]
            {
                (
                    VideoScaling.Original,
                    FormatScalingOptionLabel("Original", captureSize),
                    captureSize
                ),
                CreateScalingOption("1440p", VideoScaling.Target1440p, captureSize),
                CreateScalingOption("1080p", VideoScaling.Optimized, captureSize),
                CreateScalingOption("720p", VideoScaling.Target720p, captureSize),
            };

            return candidates
                .Where(option =>
                    option.Value == VideoScaling.Original
                    || option.Value == SelectedVideoScaling
                    || option.OutputSize != captureSize
                )
                .Select(option => new VideoScalingOption(option.Value, option.Label))
                .ToArray();
        }
    }

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

    public bool IsRecordingStorageLimitEnabled
    {
        get;
        set
        {
            if (SetEditableProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanConfigureRecordingStorageLimit));

                if (!value)
                {
                    RecordingStorageLimitInputGigabytes = RecordingStorageLimitGigabytes;
                }

                NotifyRecordingStorageLimitApplyStateChanged();
            }
        }
    }

    public bool CanConfigureRecordingStorageLimit =>
        IsEditingEnabled && IsRecordingStorageLimitEnabled;

    public bool HasPendingRecordingStorageLimitChange =>
        RecordingStorageLimitInputGigabytes != RecordingStorageLimitGigabytes;

    public bool CanApplyRecordingStorageLimit =>
        CanConfigureRecordingStorageLimit && HasPendingRecordingStorageLimitChange;

    public int RecordingStorageLimitGigabytes
    {
        get;
        set
        {
            var limitGigabytes = Math.Clamp(value, 1, MaximumRecordingStorageLimitGigabytes);

            if (SetEditableProperty(ref field, limitGigabytes))
            {
                NotifyRecordingStorageLimitApplyStateChanged();
            }
        }
    }

    public int RecordingStorageLimitInputGigabytes
    {
        get;
        set
        {
            var limitGigabytes = Math.Clamp(value, 1, MaximumRecordingStorageLimitGigabytes);

            if (!SetProperty(ref field, limitGigabytes))
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
            }

            NotifyRecordingStorageLimitApplyStateChanged();
        }
    }

    public string RecordingStorageUsageText
    {
        get
        {
            var usageText = _recordingStorageStatus.UsageBytes is { } usageBytes
                ? FormatStorageSize(usageBytes)
                : "Calculating";
            var limitText = IsRecordingStorageLimitEnabled
                ? FormatStorageSize(GetConfiguredRecordingStorageLimitBytes())
                : "Unlimited";

            return $"Managed recordings storage: {usageText} / {limitText}";
        }
    }

    public string RecordingStorageStatusText
    {
        get
        {
            if (_recordingStorageStatus.LastError is not null)
            {
                return $"Could not read managed recordings storage: {_recordingStorageStatus.LastError.Message}";
            }

            if (_recordingStorageStatus.IsCleaning)
            {
                return "Cleaning up old recordings...";
            }

            if (_recordingStorageStatus.IsRefreshing)
            {
                return "Calculating managed recordings storage...";
            }

            if (_recordingStorageStatus.UsageBytes is null)
            {
                return "Managed recordings storage has not been scanned yet.";
            }

            if (!IsRecordingStorageLimitEnabled)
            {
                return "Storage limit is disabled. PullWatch-owned recordings are still counted.";
            }

            if (IsRecordingStorageOverLimit)
            {
                return "Managed recordings are over the configured limit.";
            }

            if (IsRecordingStorageNearLimit)
            {
                return "Managed recordings are close to the configured limit.";
            }

            return "Oldest managed recordings are removed first when the limit is reached.";
        }
    }

    public double RecordingStorageUsagePercent
    {
        get
        {
            var limitBytes = GetConfiguredRecordingStorageLimitBytes();
            return limitBytes > 0 && _recordingStorageStatus.UsageBytes is { } usageBytes
                ? Math.Clamp(usageBytes * 100d / limitBytes, 0, 100)
                : 0;
        }
    }

    public bool IsRecordingStorageProgressVisible => IsRecordingStorageLimitEnabled;

    public bool IsRecordingStorageUsageIndeterminate =>
        IsRecordingStorageLimitEnabled
        && _recordingStorageStatus.UsageBytes is null
        && (_recordingStorageStatus.IsRefreshing || _recordingStorageStatus.IsCleaning);

    public bool IsRecordingStorageOverLimit =>
        IsRecordingStorageLimitEnabled
        && _recordingStorageStatus.UsageBytes is { } usageBytes
        && usageBytes > GetConfiguredRecordingStorageLimitBytes();

    public bool IsRecordingStorageNearLimit =>
        IsRecordingStorageLimitEnabled
        && !IsRecordingStorageOverLimit
        && _recordingStorageStatus.UsageBytes is { } usageBytes
        && usageBytes >= GetConfiguredRecordingStorageLimitBytes() * 0.85d;

    public void ApplyRecordingStorageStatus(RecordingStorageStatus status)
    {
        if (SetProperty(ref _recordingStorageStatus, status))
        {
            NotifyRecordingStorageUsageChanged();
        }
    }

    public bool RecordMythicPlus
    {
        get;
        set
        {
            if (SetEditableProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanConfigureMythicPlus));
            }
        }
    }

    public bool CanConfigureMythicPlus => IsEditingEnabled && RecordMythicPlus;

    public int MinimumMythicPlusKeystoneLevel
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool RecordRaidEncounters
    {
        get;
        set
        {
            if (SetEditableProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanConfigureRaidEncounters));
            }
        }
    }

    public bool CanConfigureRaidEncounters => IsEditingEnabled && RecordRaidEncounters;

    public bool RecordRaidFinder
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool RecordNormalRaid
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool RecordHeroicRaid
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool RecordMythicRaid
    {
        get;
        set => SetEditableProperty(ref field, value);
    }

    public bool StartWithWindows
    {
        get;
        set
        {
            if (!SetEditableProperty(ref field, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanStartMinimizedToTray));

            if (!value && StartMinimizedToTray)
            {
                StartMinimizedToTray = false;
            }
        }
    }

    public bool StartMinimizedToTray
    {
        get;
        set => SetEditableProperty(ref field, StartWithWindows && value);
    }

    public bool CanStartMinimizedToTray => IsEditingEnabled && StartWithWindows;

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

    public VideoScaling SelectedVideoScaling
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
                OnPropertyChanged(nameof(CanStartMinimizedToTray));
                OnPropertyChanged(nameof(CanConfigureMythicPlus));
                OnPropertyChanged(nameof(CanConfigureRaidEncounters));
                OnPropertyChanged(nameof(CanConfigureRecordingStorageLimit));
                NotifyRecordingStorageLimitApplyStateChanged();
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
            var outputSize = VideoOutputSizeCalculator.CalculateOutputSize(
                captureSize,
                SelectedVideoScaling
            );
            var bitrate = VideoBitrateCalculator.CalculateBitrate(
                outputSize,
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
                $"About {FormatFileSize(megabytes)} per minute",
                FormatEstimateSizeText(captureSize, outputSize),
                $"{SelectedFrameRate} FPS",
                $"({VideoBitrateCalculator.ToMegabitsPerSecond(bitrate)} Mbps target).",
                "Actual recording uses the WoW window size."
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

    public bool ConfirmPendingRecordingStorageLimitChangeForNavigation()
    {
        if (!HasPendingRecordingStorageLimitChange)
        {
            return true;
        }

        return _dialogs.ConfirmPendingRecordingStorageLimitChange(
            RecordingStorageLimitGigabytes,
            RecordingStorageLimitInputGigabytes
        ) switch
        {
            PendingRecordingStorageLimitChangeAction.Apply =>
                ApplyPendingRecordingStorageLimitChange(),
            PendingRecordingStorageLimitChangeAction.Discard =>
                DiscardPendingRecordingStorageLimitChange(),
            _ => false,
        };
    }

    public bool ApplyPendingRecordingStorageLimitChange()
    {
        if (!HasPendingRecordingStorageLimitChange)
        {
            return true;
        }

        if (!IsEditingEnabled)
        {
            ApplyLockedStatus();
            return false;
        }

        RecordingStorageLimitGigabytes = RecordingStorageLimitInputGigabytes;
        return true;
    }

    public bool DiscardPendingRecordingStorageLimitChange()
    {
        RecordingStorageLimitInputGigabytes = RecordingStorageLimitGigabytes;
        return true;
    }

    private async Task ExecuteCommandAsync(Func<Task<bool>> command)
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

    [RelayCommand(CanExecute = nameof(CanApplyRecordingStorageLimit))]
    private void ApplyRecordingStorageLimit()
    {
        ApplyPendingRecordingStorageLimitChange();
    }

    [RelayCommand(CanExecute = nameof(IsEditingEnabled))]
    private async Task PickWowLogsDirectoryAsync()
    {
        try
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
        catch (Exception exception)
        {
            HandleCommandFailure(exception);
        }
    }

    [RelayCommand(CanExecute = nameof(IsEditingEnabled))]
    private async Task PickRecordingsDirectoryAsync()
    {
        try
        {
            var selected = _dialogs.PickFolder(
                "Select the recordings directory",
                RecordingsDirectory
            );

            if (selected is not null)
            {
                RecordingsDirectory = selected;
                await CommitRecordingsDirectoryAsync();
            }
        }
        catch (Exception exception)
        {
            HandleCommandFailure(exception);
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

        await QueueAutosaveAsync(
            CreatePendingSettingsSave(includeWowLogsDirectory, includeRecordingsDirectory)
        );
        return !IsSaveError;
    }

    private PullWatchSettings BuildSettings(PendingSettingsSave pendingSave)
    {
        return _savedSettings with
        {
            WowLogsDirectory = Includes(pendingSave, PendingSettingsSave.WowLogsDirectory)
                ? NormalizeEmpty(WowLogsDirectory)
                : _savedSettings.WowLogsDirectory,
            RecordingsDirectory = Includes(pendingSave, PendingSettingsSave.RecordingsDirectory)
                ? NormalizeEmpty(RecordingsDirectory)
                : _savedSettings.RecordingsDirectory,
            RecordMythicPlus = RecordMythicPlus,
            RecordRaidEncounters = RecordRaidEncounters,
            RecordingFilters = _savedSettings.RecordingFilters with
            {
                MythicPlus = _savedSettings.RecordingFilters.MythicPlus with
                {
                    MinimumKeystoneLevel = MinimumMythicPlusKeystoneLevel,
                },
                RaidEncounters = _savedSettings.RecordingFilters.RaidEncounters with
                {
                    RecordRaidFinder = RecordRaidFinder,
                    RecordNormal = RecordNormalRaid,
                    RecordHeroic = RecordHeroicRaid,
                    RecordMythic = RecordMythicRaid,
                },
            },
            Video = _savedSettings.Video with
            {
                Quality = SelectedVideoQuality,
                FrameRate = SelectedFrameRate,
                Scaling = SelectedVideoScaling,
                CaptureCursor = CaptureCursor,
                ShowCaptureBorder = ShowCaptureBorder,
            },
            Audio = _savedSettings.Audio with
            {
                CaptureSystemAudio = CaptureSystemAudio,
                CaptureMicrophone = CaptureMicrophone,
            },
            Startup = _savedSettings.Startup with
            {
                StartWithWindows = StartWithWindows,
                StartMinimizedToTray = StartWithWindows && StartMinimizedToTray,
            },
            Storage = _savedSettings.Storage with
            {
                MaxUsageBytes = IsRecordingStorageLimitEnabled
                    ? GigabytesToBytes(RecordingStorageLimitGigabytes)
                    : RecordingStorageSettings.UnlimitedBytes,
            },
        };
    }

    private Task QueueAutosaveAsync(PendingSettingsSave requestedSave)
    {
        lock (_autosaveSync)
        {
            _pendingAutosave = (_pendingAutosave ?? PendingSettingsSave.None) | requestedSave;
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
            PendingSettingsSave pendingSave;

            lock (_autosaveSync)
            {
                if (_pendingAutosave is null)
                {
                    _autosaveTask = null;
                    return;
                }

                pendingSave = _pendingAutosave.Value | _retryOnNextAutosave;
                _pendingAutosave = null;
            }

            await RunAutosaveAsync(pendingSave);
        }
    }

    private async Task RunAutosaveAsync(PendingSettingsSave pendingSave)
    {
        if (!IsEditingEnabled)
        {
            ApplyLockedStatus();
            return;
        }

        var settings = BuildSettings(pendingSave);
        var shouldSyncStartupShortcut =
            Includes(pendingSave, PendingSettingsSave.StartupShortcut)
            || settings.Startup != _savedSettings.Startup;

        try
        {
            var result = await _saveSettings(settings);

            if (!result.IsSaved)
            {
                ApplySaveFailure(result, pendingSave, settings);
                return;
            }

            ApplySaveSuccess(result, pendingSave);

            if (shouldSyncStartupShortcut)
            {
                await SyncStartupShortcutAsync(result.Settings!.Startup);
            }
        }
        catch (Exception exception)
        {
            HandleAutosaveException(exception, pendingSave, settings);
        }
    }

    private void ApplySaveSuccess(SettingsSaveResult result, PendingSettingsSave pendingSave)
    {
        var savedSettings = result.Settings!;
        _savedSettings = savedSettings;

        if (
            Includes(pendingSave, PendingSettingsSave.WowLogsDirectory)
            && PathsMatchSaved(WowLogsDirectory, savedSettings.WowLogsDirectory)
        )
        {
            IsWowLogsDirectoryPending = false;
            SetRetryOnNextAutosave(PendingSettingsSave.WowLogsDirectory, shouldRetry: false);
            SetLoadedValue(() => WowLogsDirectory = savedSettings.WowLogsDirectory);
            WowLogsDirectoryError = null;
        }

        if (
            Includes(pendingSave, PendingSettingsSave.RecordingsDirectory)
            && PathsMatchSaved(RecordingsDirectory, savedSettings.RecordingsDirectory)
        )
        {
            IsRecordingsDirectoryPending = false;
            SetRetryOnNextAutosave(PendingSettingsSave.RecordingsDirectory, shouldRetry: false);
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

    private async Task SyncStartupShortcutAsync(StartupSettings settings)
    {
        try
        {
            await _windowsStartupShortcut.SyncAsync(settings);
            SetRetryOnNextAutosave(PendingSettingsSave.StartupShortcut, shouldRetry: false);
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or COMException
            )
        {
            SetRetryOnNextAutosave(PendingSettingsSave.StartupShortcut, shouldRetry: true);
            IsSaveError = true;
            SaveMessage =
                $"Settings saved, but Windows startup could not be updated: {exception.Message}";
        }
    }

    private void ApplySaveFailure(
        SettingsSaveResult result,
        PendingSettingsSave pendingSave,
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
            MarkIncludedPathsForRetry(pendingSave, attemptedSettings);
            SaveMessage = $"Could not save settings: {result.Error?.Message}";
            return;
        }

        ClearIncludedPathRetries(pendingSave);
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
        HandleAutosaveException(exception, PendingSettingsSave.None, attemptedSettings: null);
    }

    private void HandleAutosaveException(
        Exception exception,
        PendingSettingsSave pendingSave,
        PullWatchSettings? attemptedSettings
    )
    {
        IsSaveError = true;
        MarkIncludedPathsForRetry(pendingSave, attemptedSettings);
        SaveMessage = $"Could not save settings: {exception.Message}";
    }

    private void LoadSettings(PullWatchSettings settings)
    {
        SetLoadedValue(() =>
        {
            WowLogsDirectory = settings.WowLogsDirectory;
            RecordingsDirectory = settings.RecordingsDirectory;
            IsRecordingStorageLimitEnabled = settings.Storage.IsLimitEnabled;
            RecordingStorageLimitGigabytes = settings.Storage.IsLimitEnabled
                ? BytesToGigabytes(settings.Storage.MaxUsageBytes)
                : DefaultRecordingStorageLimitGigabytes;
            RecordingStorageLimitInputGigabytes = RecordingStorageLimitGigabytes;
            RecordMythicPlus = settings.RecordMythicPlus;
            MinimumMythicPlusKeystoneLevel = settings
                .RecordingFilters
                .MythicPlus
                .MinimumKeystoneLevel;
            RecordRaidEncounters = settings.RecordRaidEncounters;
            RecordRaidFinder = settings.RecordingFilters.RaidEncounters.RecordRaidFinder;
            RecordNormalRaid = settings.RecordingFilters.RaidEncounters.RecordNormal;
            RecordHeroicRaid = settings.RecordingFilters.RaidEncounters.RecordHeroic;
            RecordMythicRaid = settings.RecordingFilters.RaidEncounters.RecordMythic;
            StartWithWindows = settings.Startup.StartWithWindows;
            StartMinimizedToTray =
                settings.Startup.StartWithWindows && settings.Startup.StartMinimizedToTray;
            SelectedVideoQuality = settings.Video.Quality;
            SelectedFrameRate = settings.Video.FrameRate;
            SelectedVideoScaling = settings.Video.Scaling;
            CaptureSystemAudio = settings.Audio.CaptureSystemAudio;
            CaptureMicrophone = settings.Audio.CaptureMicrophone;
            CaptureCursor = settings.Video.CaptureCursor;
            ShowCaptureBorder = settings.Video.ShowCaptureBorder;
        });

        OnPropertyChanged(nameof(EstimatedRecordingSize));
        NotifyRecordingStorageUsageChanged();
    }

    private void ClearErrors()
    {
        WowLogsDirectoryError = null;
        RecordingsDirectoryError = null;
    }

    private bool SetEditableProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null
    )
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
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
                _ = QueueAutosaveAsync(PendingSettingsSave.None);
            }
        }

        if (ShouldRefreshEstimate(propertyName))
        {
            OnPropertyChanged(nameof(EstimatedRecordingSize));
        }

        if (propertyName == nameof(SelectedVideoScaling))
        {
            OnPropertyChanged(nameof(VideoScalingOptions));
        }

        if (ShouldRefreshRecordingStorageUsage(propertyName))
        {
            NotifyRecordingStorageUsageChanged();
        }

        return true;
    }

    private bool MarkPendingPath(string? propertyName)
    {
        if (propertyName == nameof(WowLogsDirectory))
        {
            IsWowLogsDirectoryPending = true;
            SetRetryOnNextAutosave(PendingSettingsSave.WowLogsDirectory, shouldRetry: false);
            return true;
        }

        if (propertyName == nameof(RecordingsDirectory))
        {
            IsRecordingsDirectoryPending = true;
            SetRetryOnNextAutosave(PendingSettingsSave.RecordingsDirectory, shouldRetry: false);
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
                || HasPendingRecordingStorageLimitChange
                || _pendingAutosave is not null
                || _autosaveTask is { IsCompleted: false };
        }
    }

    private void NotifyCommandStatesChanged()
    {
        CommitWowLogsDirectoryCommand.NotifyCanExecuteChanged();
        CommitRecordingsDirectoryCommand.NotifyCanExecuteChanged();
        PickWowLogsDirectoryCommand.NotifyCanExecuteChanged();
        PickRecordingsDirectoryCommand.NotifyCanExecuteChanged();
        ApplyRecordingStorageLimitCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRecordingStorageLimitApplyStateChanged()
    {
        OnPropertyChanged(nameof(HasPendingRecordingStorageLimitChange));
        OnPropertyChanged(nameof(CanApplyRecordingStorageLimit));
        ApplyRecordingStorageLimitCommand.NotifyCanExecuteChanged();
    }

    private void MarkIncludedPathsForRetry(
        PendingSettingsSave pendingSave,
        PullWatchSettings? attemptedSettings
    )
    {
        if (
            Includes(pendingSave, PendingSettingsSave.WowLogsDirectory)
            && IsWowLogsDirectoryPending
            && attemptedSettings is not null
            && PathsMatchSaved(WowLogsDirectory, attemptedSettings.WowLogsDirectory)
        )
        {
            SetRetryOnNextAutosave(PendingSettingsSave.WowLogsDirectory, shouldRetry: true);
        }

        if (
            Includes(pendingSave, PendingSettingsSave.RecordingsDirectory)
            && IsRecordingsDirectoryPending
            && attemptedSettings is not null
            && PathsMatchSaved(RecordingsDirectory, attemptedSettings.RecordingsDirectory)
        )
        {
            SetRetryOnNextAutosave(PendingSettingsSave.RecordingsDirectory, shouldRetry: true);
        }
    }

    private void ClearIncludedPathRetries(PendingSettingsSave pendingSave)
    {
        if (Includes(pendingSave, PendingSettingsSave.WowLogsDirectory))
        {
            SetRetryOnNextAutosave(PendingSettingsSave.WowLogsDirectory, shouldRetry: false);
        }

        if (Includes(pendingSave, PendingSettingsSave.RecordingsDirectory))
        {
            SetRetryOnNextAutosave(PendingSettingsSave.RecordingsDirectory, shouldRetry: false);
        }
    }

    private void SetRetryOnNextAutosave(PendingSettingsSave scope, bool shouldRetry)
    {
        _retryOnNextAutosave = shouldRetry
            ? _retryOnNextAutosave | scope
            : _retryOnNextAutosave & ~scope;
    }

    private static PendingSettingsSave CreatePendingSettingsSave(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        var pendingSave = PendingSettingsSave.None;

        if (includeWowLogsDirectory)
        {
            pendingSave |= PendingSettingsSave.WowLogsDirectory;
        }

        if (includeRecordingsDirectory)
        {
            pendingSave |= PendingSettingsSave.RecordingsDirectory;
        }

        return pendingSave;
    }

    private static bool Includes(PendingSettingsSave pendingSave, PendingSettingsSave scope)
    {
        return (pendingSave & scope) != PendingSettingsSave.None;
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
                or nameof(SelectedVideoScaling)
                or nameof(CaptureSystemAudio)
                or nameof(CaptureMicrophone);
    }

    private static bool ShouldRefreshRecordingStorageUsage(string? propertyName)
    {
        return propertyName
            is nameof(IsRecordingStorageLimitEnabled)
                or nameof(RecordingStorageLimitGigabytes);
    }

    private void NotifyRecordingStorageUsageChanged()
    {
        OnPropertyChanged(nameof(RecordingStorageUsageText));
        OnPropertyChanged(nameof(RecordingStorageStatusText));
        OnPropertyChanged(nameof(RecordingStorageUsagePercent));
        OnPropertyChanged(nameof(IsRecordingStorageProgressVisible));
        OnPropertyChanged(nameof(IsRecordingStorageUsageIndeterminate));
        OnPropertyChanged(nameof(IsRecordingStorageOverLimit));
        OnPropertyChanged(nameof(IsRecordingStorageNearLimit));
    }

    private long GetConfiguredRecordingStorageLimitBytes()
    {
        return IsRecordingStorageLimitEnabled
            ? GigabytesToBytes(RecordingStorageLimitGigabytes)
            : RecordingStorageSettings.UnlimitedBytes;
    }

    private static long GigabytesToBytes(int gigabytes)
    {
        return Math.Clamp(gigabytes, 1, MaximumRecordingStorageLimitGigabytes) * BytesPerGigabyte;
    }

    private static int BytesToGigabytes(long bytes)
    {
        if (bytes <= 0)
        {
            return DefaultRecordingStorageLimitGigabytes;
        }

        return Math.Clamp(
            (int)Math.Ceiling(bytes / (double)BytesPerGigabyte),
            1,
            MaximumRecordingStorageLimitGigabytes
        );
    }

    private static string FormatStorageSize(long bytes)
    {
        bytes = Math.Max(0, bytes);

        if (bytes >= BytesPerGigabyte)
        {
            return $"{bytes / (double)BytesPerGigabyte:0.#} GB";
        }

        if (bytes >= BytesPerMegabyte)
        {
            return $"{bytes / (double)BytesPerMegabyte:0.#} MB";
        }

        return $"{bytes / (double)BytesPerKilobyte:0.#} KB";
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

    private static string FormatEstimateSizeText(
        VideoCaptureSize captureSize,
        VideoCaptureSize outputSize
    )
    {
        if (outputSize == captureSize)
        {
            return $"at estimated {FormatCaptureSize(captureSize)} capture,";
        }

        return $"at estimated {FormatCaptureSize(outputSize)} output from {FormatCaptureSize(captureSize)} capture,";
    }

    private static (
        VideoScaling Value,
        string Label,
        VideoCaptureSize OutputSize
    ) CreateScalingOption(string label, VideoScaling scaling, VideoCaptureSize captureSize)
    {
        var outputSize = VideoOutputSizeCalculator.CalculateOutputSize(captureSize, scaling);
        return (scaling, FormatScalingOptionLabel(label, outputSize), outputSize);
    }

    private static string FormatScalingOptionLabel(string label, VideoCaptureSize size)
    {
        return $"{label} ({FormatCaptureSize(size)} estimated)";
    }

    private static string FormatCaptureSize(VideoCaptureSize size)
    {
        return $"{size.Width}x{size.Height}";
    }

    private static VideoCaptureSize GetEstimatedCaptureSize()
    {
        return WowWindowCaptureSizeDetector.TryGetCurrentCaptureSize(out var wowCaptureSize)
            ? wowCaptureSize
            : GetPrimaryDisplayCaptureSize();
    }

    private static VideoCaptureSize GetPrimaryDisplayCaptureSize()
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds;

        return bounds is { Width: > 0, Height: > 0 }
            ? new VideoCaptureSize(bounds.Value.Width, bounds.Value.Height)
            : FallbackEstimateCaptureSize;
    }

    [Flags]
    private enum PendingSettingsSave
    {
        None = 0,
        WowLogsDirectory = 1,
        RecordingsDirectory = 2,
        StartupShortcut = 4,
    }

    private sealed class NoOpWindowsStartupShortcut : IWindowsStartupShortcut
    {
        public static NoOpWindowsStartupShortcut Instance { get; } = new();

        public Task SyncAsync(StartupSettings settings)
        {
            return Task.CompletedTask;
        }
    }
}

public sealed record VideoQualityOption(VideoQuality Value, string Label);

public sealed record FrameRateOption(int Value, string Label);

public sealed record VideoScalingOption(VideoScaling Value, string Label);
