using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PullWatch;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string SettingsNotificationId = "settings-save";
    private const string SettingsSavedMessage = "Settings saved.";
    private const string FixHighlightedSettingsMessage = "Fix the highlighted settings.";
    private const string SettingsLockedMessage = "Settings are locked while recording.";

    private const bool IsMicrophoneCaptureAvailable = false;

    private static readonly TimeSpan SettingsSuccessNotificationDuration = TimeSpan.FromSeconds(5);

    private readonly SettingsAutosaveCoordinator _autosave;
    private readonly VideoSettingsPresenter _videoPresenter;
    private readonly RecordingStoragePresenter _recordingStoragePresenter;
    private readonly Func<Task> _testVideoEncoding;
    private readonly ISettingsDialogs _dialogs;
    private readonly IWindowsStartupShortcut _windowsStartupShortcut;
    private readonly NotificationCenterViewModel? _notifications;
    private readonly IUiDispatcher? _notificationDispatcher;
    private readonly TimeSpan _settingsSuccessNotificationDuration;
    private CancellationTokenSource? _settingsSuccessNotificationCancellation;
    private SettingsNotificationKind? _activeSettingsNotificationKind;
    private bool _isLoading;

    public SettingsViewModel(
        ApplicationStatus initialStatus,
        Func<PullWatchSettings, Task<SettingsSaveResult>> saveSettings,
        ISettingsDialogs dialogs,
        Func<VideoCaptureSize>? getEstimateCaptureSize = null,
        Func<Task>? testVideoEncoding = null,
        IWindowsStartupShortcut? windowsStartupShortcut = null,
        RecordingStorageStatus? initialRecordingStorageStatus = null,
        NotificationCenterViewModel? notifications = null,
        IUiDispatcher? notificationDispatcher = null,
        TimeSpan? settingsSuccessNotificationDuration = null
    )
    {
        _testVideoEncoding = testVideoEncoding ?? TestVideoEncodingUnavailableAsync;
        _dialogs = dialogs;
        _videoPresenter = new VideoSettingsPresenter(
            getEstimateCaptureSize ?? VideoSettingsPresenter.GetEstimatedCaptureSize
        );
        _windowsStartupShortcut = windowsStartupShortcut ?? NoOpWindowsStartupShortcut.Instance;
        _recordingStoragePresenter = new RecordingStoragePresenter(
            initialRecordingStorageStatus ?? RecordingStorageStatus.Initial
        );
        _notifications = notifications;
        _notificationDispatcher = notificationDispatcher;
        _settingsSuccessNotificationDuration =
            settingsSuccessNotificationDuration ?? SettingsSuccessNotificationDuration;
        var savedSettings = initialStatus.EffectiveSettings ?? new PullWatchSettings();
        _autosave = new SettingsAutosaveCoordinator(
            savedSettings,
            () => IsEditingEnabled,
            BuildSettings,
            saveSettings,
            HandleAutosaveResultAsync
        );
        CommitWowLogsDirectoryCommand = new AsyncRelayCommand(
            () => ExecuteCommandAsync(CommitWowLogsDirectoryAsync),
            () => IsEditingEnabled
        );
        CommitRecordingsDirectoryCommand = new AsyncRelayCommand(
            () => ExecuteCommandAsync(CommitRecordingsDirectoryAsync),
            () => IsEditingEnabled
        );
        TestVideoEncodingCommand = new AsyncRelayCommand(
            TestVideoEncodingAsync,
            () => CanTestVideoEncoding
        );
        LoadSettings(savedSettings);
        ApplyStatus(initialStatus);
    }

    public IAsyncRelayCommand CommitWowLogsDirectoryCommand { get; }

    public IAsyncRelayCommand CommitRecordingsDirectoryCommand { get; }

    public IAsyncRelayCommand TestVideoEncodingCommand { get; }

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

    public IReadOnlyList<VideoScalingOption> VideoScalingOptions =>
        _videoPresenter.GetScalingOptions(SelectedVideoScaling);

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
            var limitGigabytes = Math.Clamp(
                value,
                1,
                RecordingStoragePresenter.MaximumLimitGigabytes
            );

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
            var limitGigabytes = Math.Clamp(
                value,
                1,
                RecordingStoragePresenter.MaximumLimitGigabytes
            );

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

    public string RecordingStorageUsageText =>
        _recordingStoragePresenter.GetUsageText(
            IsRecordingStorageLimitEnabled,
            GetConfiguredRecordingStorageLimitBytes()
        );

    public string RecordingStorageStatusText =>
        _recordingStoragePresenter.GetStatusText(
            IsRecordingStorageLimitEnabled,
            GetConfiguredRecordingStorageLimitBytes()
        );

    public double RecordingStorageUsagePercent =>
        _recordingStoragePresenter.GetUsagePercent(GetConfiguredRecordingStorageLimitBytes());

    public bool IsRecordingStorageProgressVisible => IsRecordingStorageLimitEnabled;

    public bool IsRecordingStorageUsageIndeterminate =>
        _recordingStoragePresenter.IsUsageIndeterminate(IsRecordingStorageLimitEnabled);

    public bool IsRecordingStorageOverLimit =>
        _recordingStoragePresenter.IsOverLimit(
            IsRecordingStorageLimitEnabled,
            GetConfiguredRecordingStorageLimitBytes()
        );

    public bool IsRecordingStorageNearLimit =>
        _recordingStoragePresenter.IsNearLimit(
            IsRecordingStorageLimitEnabled,
            GetConfiguredRecordingStorageLimitBytes()
        );

    public void ApplyRecordingStorageStatus(RecordingStorageStatus status)
    {
        if (_recordingStoragePresenter.ApplyStatus(status))
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

    public IReadOnlyList<VideoProfileOption> VideoProfileOptions =>
        VideoProfileSelectionPolicy
            .GetPassingProfilesInPriorityOrder(_autosave.SavedSettings.EncoderCalibration.Results)
            .Select(profile => new VideoProfileOption(
                profile,
                VideoProfileFormatter.FormatDisplayName(profile)
            ))
            .ToArray();

    public bool HasVideoProfileOptions => VideoProfileOptions.Count > 0;

    public bool CanChooseVideoProfile => IsEditingEnabled && VideoProfileOptions.Count > 1;

    public VideoProfileSelection? SelectedVideoProfile
    {
        get;
        set
        {
            if (value is not null && !IsSelectableVideoProfile(value))
            {
                return;
            }

            if (SetEditableProperty(ref field, value))
            {
                NotifyVideoProfileSelectionChanged();
            }
        }
    }

    public string VideoEncodingSummary =>
        SelectedVideoProfile is null
            ? "Not tested"
            : VideoProfileFormatter.FormatDisplayName(SelectedVideoProfile);

    public string VideoEncodingStatus =>
        SelectedVideoProfile is null
            ? "PullWatch needs to test video encoding before recording."
            : "Ready for recording";

    public bool IsTestingVideoEncoding
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CanTestVideoEncoding));
                OnPropertyChanged(nameof(TestVideoEncodingButtonText));
                TestVideoEncodingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanTestVideoEncoding => IsEditingEnabled && !IsTestingVideoEncoding;

    public string TestVideoEncodingButtonText =>
        IsTestingVideoEncoding ? "Testing video encoding..."
        : SelectedVideoProfile is null ? "Test video encoding"
        : "Retest";

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
        set => SetEditableProperty(ref field, value && IsMicrophoneCaptureAvailable);
    }

    public bool CanCaptureMicrophone => IsEditingEnabled && IsMicrophoneCaptureAvailable;

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
                OnPropertyChanged(nameof(CanTestVideoEncoding));
                OnPropertyChanged(nameof(CanChooseVideoProfile));
                OnPropertyChanged(nameof(CanConfigureMythicPlus));
                OnPropertyChanged(nameof(CanConfigureRaidEncounters));
                OnPropertyChanged(nameof(CanConfigureRecordingStorageLimit));
                OnPropertyChanged(nameof(CanCaptureMicrophone));
                NotifyRecordingStorageLimitApplyStateChanged();
                TestVideoEncodingCommand.NotifyCanExecuteChanged();
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
        private set
        {
            if (SetProperty(ref field, value))
            {
                UpdateSettingsNotification();
            }
        }
    }

    public bool IsSaveError
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                UpdateSettingsNotification();
            }
        }
    }

    public string EstimatedRecordingSize =>
        _videoPresenter.GetEstimatedRecordingSize(
            SelectedVideoProfile,
            SelectedVideoQuality,
            SelectedFrameRate,
            SelectedVideoScaling,
            CaptureSystemAudio,
            CaptureMicrophone
        );

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
            && status.EffectiveSettings != _autosave.SavedSettings
            && !HasPendingLocalChanges()
        )
        {
            _autosave.UpdateSavedSettings(status.EffectiveSettings);
            LoadSettings(status.EffectiveSettings);
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

    public async Task TestVideoEncodingAsync()
    {
        if (!CanTestVideoEncoding)
        {
            ApplyLockedStatus();
            return;
        }

        IsTestingVideoEncoding = true;

        try
        {
            await _testVideoEncoding();
        }
        catch (Exception exception)
        {
            IsSaveError = true;
            SaveMessage = $"Video encoding test failed: {exception.Message}";
        }
        finally
        {
            IsTestingVideoEncoding = false;
        }
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
            CreateSettingsSaveScope(includeWowLogsDirectory, includeRecordingsDirectory)
        );
        return !IsSaveError;
    }

    private PullWatchSettings BuildSettings(
        PullWatchSettings savedSettings,
        SettingsSaveScope saveScope
    )
    {
        return savedSettings with
        {
            WowLogsDirectory = SettingsAutosaveCoordinator.Includes(
                saveScope,
                SettingsSaveScope.WowLogsDirectory
            )
                ? NormalizeEmpty(WowLogsDirectory)
                : savedSettings.WowLogsDirectory,
            RecordingsDirectory = SettingsAutosaveCoordinator.Includes(
                saveScope,
                SettingsSaveScope.RecordingsDirectory
            )
                ? NormalizeEmpty(RecordingsDirectory)
                : savedSettings.RecordingsDirectory,
            RecordMythicPlus = RecordMythicPlus,
            RecordRaidEncounters = RecordRaidEncounters,
            RecordingFilters = savedSettings.RecordingFilters with
            {
                MythicPlus = savedSettings.RecordingFilters.MythicPlus with
                {
                    MinimumKeystoneLevel = MinimumMythicPlusKeystoneLevel,
                },
                RaidEncounters = savedSettings.RecordingFilters.RaidEncounters with
                {
                    RecordRaidFinder = RecordRaidFinder,
                    RecordNormal = RecordNormalRaid,
                    RecordHeroic = RecordHeroicRaid,
                    RecordMythic = RecordMythicRaid,
                },
            },
            Video = savedSettings.Video with
            {
                SelectedProfile = SelectedVideoProfile,
                Quality = SelectedVideoQuality,
                FrameRate = SelectedFrameRate,
                Scaling = SelectedVideoScaling,
                CaptureCursor = CaptureCursor,
                ShowCaptureBorder = ShowCaptureBorder,
            },
            Audio = savedSettings.Audio with
            {
                CaptureSystemAudio = CaptureSystemAudio,
                CaptureMicrophone = false,
            },
            Startup = savedSettings.Startup with
            {
                StartWithWindows = StartWithWindows,
                StartMinimizedToTray = StartWithWindows && StartMinimizedToTray,
            },
            Storage = savedSettings.Storage with
            {
                MaxUsageBytes = IsRecordingStorageLimitEnabled
                    ? RecordingStoragePresenter.GigabytesToBytes(RecordingStorageLimitGigabytes)
                    : RecordingStorageSettings.UnlimitedBytes,
            },
        };
    }

    private Task QueueAutosaveAsync(SettingsSaveScope requestedScope)
    {
        return _autosave.QueueSaveAsync(requestedScope);
    }

    private async Task HandleAutosaveResultAsync(SettingsAutosaveOutcome outcome)
    {
        if (outcome.WasSkipped)
        {
            ApplyLockedStatus();
            return;
        }

        if (outcome.Exception is not null)
        {
            HandleAutosaveException(outcome.Exception, outcome.Scope, outcome.AttemptedSettings);
            return;
        }

        try
        {
            var result = outcome.SaveResult!;

            if (!result.WasPersisted)
            {
                ApplySaveFailure(result, outcome.Scope, outcome.AttemptedSettings);
                return;
            }

            ApplySaveSuccess(result, outcome.Scope);

            if (outcome.ShouldSyncStartupShortcut)
            {
                await SyncStartupShortcutAsync(result.Settings!.Startup);
            }
        }
        catch (Exception exception)
        {
            HandleAutosaveException(exception, outcome.Scope, outcome.AttemptedSettings);
        }
    }

    private void ApplySaveSuccess(SettingsSaveResult result, SettingsSaveScope saveScope)
    {
        var savedSettings = result.Settings!;

        if (
            SettingsAutosaveCoordinator.Includes(saveScope, SettingsSaveScope.WowLogsDirectory)
            && PathsMatchSaved(WowLogsDirectory, savedSettings.WowLogsDirectory)
        )
        {
            IsWowLogsDirectoryPending = false;
            SetRetryOnNextAutosave(SettingsSaveScope.WowLogsDirectory, shouldRetry: false);
            SetLoadedValue(() => WowLogsDirectory = savedSettings.WowLogsDirectory);
            WowLogsDirectoryError = null;
        }

        if (
            SettingsAutosaveCoordinator.Includes(saveScope, SettingsSaveScope.RecordingsDirectory)
            && PathsMatchSaved(RecordingsDirectory, savedSettings.RecordingsDirectory)
        )
        {
            IsRecordingsDirectoryPending = false;
            SetRetryOnNextAutosave(SettingsSaveScope.RecordingsDirectory, shouldRetry: false);
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

    private void UpdateSettingsNotification()
    {
        if (_notifications is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SaveMessage))
        {
            if (_activeSettingsNotificationKind == SettingsNotificationKind.Error)
            {
                ClearSettingsNotification();
            }

            return;
        }

        if (IsSaveError)
        {
            ShowSettingsErrorNotification(SaveMessage);
            return;
        }

        if (SaveMessage == SettingsSavedMessage)
        {
            ShowSettingsSuccessNotification();
            return;
        }

        if (_activeSettingsNotificationKind == SettingsNotificationKind.Error)
        {
            ClearSettingsNotification();
        }
    }

    private void ShowSettingsSuccessNotification()
    {
        if (
            _notifications is null
            || _activeSettingsNotificationKind == SettingsNotificationKind.Success
        )
        {
            return;
        }

        CancelSettingsSuccessNotificationTimer();
        _activeSettingsNotificationKind = SettingsNotificationKind.Success;
        _notifications.ShowOrUpdate(
            SettingsNotificationId,
            new NotificationContent(
                NotificationSeverity.Success,
                SettingsSavedMessage,
                "Your changes have been saved.",
                Dismissed: ClearSettingsNotificationState
            )
        );

        _settingsSuccessNotificationCancellation = new CancellationTokenSource();
        _ = DismissSettingsSuccessNotificationAfterDelayAsync(
            _settingsSuccessNotificationCancellation.Token
        );
    }

    private void ShowSettingsErrorNotification(string message)
    {
        if (_notifications is null)
        {
            return;
        }

        CancelSettingsSuccessNotificationTimer();
        _activeSettingsNotificationKind = SettingsNotificationKind.Error;
        _notifications.ShowOrUpdate(
            SettingsNotificationId,
            new NotificationContent(
                NotificationSeverity.Error,
                "Settings need attention",
                message,
                Dismissed: ClearSettingsNotificationState
            )
        );
    }

    private async Task DismissSettingsSuccessNotificationAfterDelayAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(_settingsSuccessNotificationDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PostNotificationUpdate(() =>
        {
            if (
                cancellationToken.IsCancellationRequested
                || _activeSettingsNotificationKind != SettingsNotificationKind.Success
            )
            {
                return;
            }

            _notifications?.Dismiss(SettingsNotificationId);
            ClearSettingsNotificationState();
        });
    }

    private void ClearSettingsNotification()
    {
        CancelSettingsSuccessNotificationTimer();
        _notifications?.Dismiss(SettingsNotificationId);
        ClearSettingsNotificationState();
    }

    private void ClearSettingsNotificationState()
    {
        CancelSettingsSuccessNotificationTimer();
        _activeSettingsNotificationKind = null;
    }

    private void CancelSettingsSuccessNotificationTimer()
    {
        var cancellation = Interlocked.Exchange(ref _settingsSuccessNotificationCancellation, null);

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void PostNotificationUpdate(Action update)
    {
        if (_notificationDispatcher is null)
        {
            update();
            return;
        }

        _notificationDispatcher.Post(update);
    }

    private async Task SyncStartupShortcutAsync(StartupSettings settings)
    {
        try
        {
            await _windowsStartupShortcut.SyncAsync(settings);
            SetRetryOnNextAutosave(SettingsSaveScope.StartupShortcut, shouldRetry: false);
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or COMException
            )
        {
            SetRetryOnNextAutosave(SettingsSaveScope.StartupShortcut, shouldRetry: true);
            IsSaveError = true;
            SaveMessage =
                $"Settings saved, but Windows startup could not be updated: {exception.Message}";
        }
    }

    private void ApplySaveFailure(
        SettingsSaveResult result,
        SettingsSaveScope saveScope,
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
            MarkIncludedPathsForRetry(saveScope, attemptedSettings);
            SaveMessage = $"Could not save settings: {result.Error?.Message}";
            return;
        }

        ClearIncludedPathRetries(saveScope);
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
        HandleAutosaveException(exception, SettingsSaveScope.None, attemptedSettings: null);
    }

    private void HandleAutosaveException(
        Exception exception,
        SettingsSaveScope saveScope,
        PullWatchSettings? attemptedSettings
    )
    {
        IsSaveError = true;
        MarkIncludedPathsForRetry(saveScope, attemptedSettings);
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
                ? RecordingStoragePresenter.BytesToGigabytes(settings.Storage.MaxUsageBytes)
                : RecordingStoragePresenter.DefaultLimitGigabytes;
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
            SelectedVideoProfile =
                settings.Video.SelectedProfile is { } selectedProfile
                && IsSelectableVideoProfile(selectedProfile)
                    ? selectedProfile
                    : null;
            OnPropertyChanged(nameof(VideoProfileOptions));
            OnPropertyChanged(nameof(HasVideoProfileOptions));
            OnPropertyChanged(nameof(CanChooseVideoProfile));
            SelectedVideoQuality = settings.Video.Quality;
            SelectedFrameRate = settings.Video.FrameRate;
            SelectedVideoScaling = settings.Video.Scaling;
            CaptureSystemAudio = settings.Audio.CaptureSystemAudio;
            CaptureMicrophone = false;
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
                _ = QueueAutosaveAsync(SettingsSaveScope.None);
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
            SetRetryOnNextAutosave(SettingsSaveScope.WowLogsDirectory, shouldRetry: false);
            return true;
        }

        if (propertyName == nameof(RecordingsDirectory))
        {
            IsRecordingsDirectoryPending = true;
            SetRetryOnNextAutosave(SettingsSaveScope.RecordingsDirectory, shouldRetry: false);
            return true;
        }

        return false;
    }

    private bool IsSelectableVideoProfile(VideoProfileSelection profile)
    {
        return VideoProfileOptions.Any(option => option.Value == profile);
    }

    private void NotifyVideoProfileSelectionChanged()
    {
        OnPropertyChanged(nameof(VideoEncodingSummary));
        OnPropertyChanged(nameof(VideoEncodingStatus));
        OnPropertyChanged(nameof(TestVideoEncodingButtonText));
        OnPropertyChanged(nameof(EstimatedRecordingSize));
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
        return IsWowLogsDirectoryPending
            || IsRecordingsDirectoryPending
            || HasPendingRecordingStorageLimitChange
            || _autosave.HasPendingSave;
    }

    private void NotifyCommandStatesChanged()
    {
        CommitWowLogsDirectoryCommand.NotifyCanExecuteChanged();
        CommitRecordingsDirectoryCommand.NotifyCanExecuteChanged();
        PickWowLogsDirectoryCommand.NotifyCanExecuteChanged();
        PickRecordingsDirectoryCommand.NotifyCanExecuteChanged();
        ApplyRecordingStorageLimitCommand.NotifyCanExecuteChanged();
        TestVideoEncodingCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRecordingStorageLimitApplyStateChanged()
    {
        OnPropertyChanged(nameof(HasPendingRecordingStorageLimitChange));
        OnPropertyChanged(nameof(CanApplyRecordingStorageLimit));
        ApplyRecordingStorageLimitCommand.NotifyCanExecuteChanged();
    }

    private void MarkIncludedPathsForRetry(
        SettingsSaveScope saveScope,
        PullWatchSettings? attemptedSettings
    )
    {
        if (
            SettingsAutosaveCoordinator.Includes(saveScope, SettingsSaveScope.WowLogsDirectory)
            && IsWowLogsDirectoryPending
            && attemptedSettings is not null
            && PathsMatchSaved(WowLogsDirectory, attemptedSettings.WowLogsDirectory)
        )
        {
            SetRetryOnNextAutosave(SettingsSaveScope.WowLogsDirectory, shouldRetry: true);
        }

        if (
            SettingsAutosaveCoordinator.Includes(saveScope, SettingsSaveScope.RecordingsDirectory)
            && IsRecordingsDirectoryPending
            && attemptedSettings is not null
            && PathsMatchSaved(RecordingsDirectory, attemptedSettings.RecordingsDirectory)
        )
        {
            SetRetryOnNextAutosave(SettingsSaveScope.RecordingsDirectory, shouldRetry: true);
        }
    }

    private void ClearIncludedPathRetries(SettingsSaveScope saveScope)
    {
        if (SettingsAutosaveCoordinator.Includes(saveScope, SettingsSaveScope.WowLogsDirectory))
        {
            SetRetryOnNextAutosave(SettingsSaveScope.WowLogsDirectory, shouldRetry: false);
        }

        if (SettingsAutosaveCoordinator.Includes(saveScope, SettingsSaveScope.RecordingsDirectory))
        {
            SetRetryOnNextAutosave(SettingsSaveScope.RecordingsDirectory, shouldRetry: false);
        }
    }

    private void SetRetryOnNextAutosave(SettingsSaveScope scope, bool shouldRetry)
    {
        _autosave.SetRetryOnNextSave(scope, shouldRetry);
    }

    private static SettingsSaveScope CreateSettingsSaveScope(
        bool includeWowLogsDirectory,
        bool includeRecordingsDirectory
    )
    {
        var saveScope = SettingsSaveScope.None;

        if (includeWowLogsDirectory)
        {
            saveScope |= SettingsSaveScope.WowLogsDirectory;
        }

        if (includeRecordingsDirectory)
        {
            saveScope |= SettingsSaveScope.RecordingsDirectory;
        }

        return saveScope;
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
            is nameof(SelectedVideoProfile)
                or nameof(SelectedVideoQuality)
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
            ? RecordingStoragePresenter.GigabytesToBytes(RecordingStorageLimitGigabytes)
            : RecordingStorageSettings.UnlimitedBytes;
    }

    private static Task TestVideoEncodingUnavailableAsync()
    {
        return Task.FromException(
            new InvalidOperationException("Video encoding testing is not available.")
        );
    }

    private enum SettingsNotificationKind
    {
        Success,
        Error,
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

public sealed record VideoProfileOption(VideoProfileSelection Value, string Label);

public sealed record FrameRateOption(int Value, string Label);

public sealed record VideoScalingOption(VideoScaling Value, string Label);
