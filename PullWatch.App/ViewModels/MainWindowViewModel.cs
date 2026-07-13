using System.Windows;
using System.Windows.Media;

namespace PullWatch;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const double ExpandedSidebarWidth = 230;
    private const double CollapsedSidebarWidth = 76;

    private readonly ApplicationController _controller;
    private readonly IUiDispatcher _dispatcher;
    private readonly InMemoryLogProvider _logs;
    private readonly FavoriteStorageWarningTracker _favoriteStorageWarnings;

    private NavigationItemViewModel _selectedNavigationItem = null!;

    public NavigationItemViewModel SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }

            if (ReferenceEquals(_selectedNavigationItem, value))
            {
                return;
            }

            if (
                IsLeavingSettings(value)
                && !Settings.ConfirmPendingRecordingStorageLimitChangeForNavigation()
            )
            {
                OnPropertyChanged();
                return;
            }

            SetProperty(ref _selectedNavigationItem, value);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded))]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    [NotifyPropertyChangedFor(nameof(SidebarToggleToolTip))]
    [NotifyPropertyChangedFor(nameof(SidebarToggleIcon))]
    private bool _isSidebarCollapsed;

    internal MainWindowViewModel(
        ApplicationController controller,
        IUiDispatcher dispatcher,
        ISettingsDialogs settingsDialogs,
        InMemoryLogProvider logs,
        IDiagnosticsDialogs diagnosticsDialogs,
        IRecordingDialogs recordingDialogs,
        Func<Task> testVideoEncoding,
        IWindowsStartupShortcut windowsStartupShortcut,
        IApplicationUpdater applicationUpdater,
        Action requestShutdownForUpdate,
        bool showSettingsOnStartup = false
    )
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _logs = logs;
        _favoriteStorageWarnings = new FavoriteStorageWarningTracker(
            controller.RecordingStorageStatus
        );
        Notifications = new NotificationCenterViewModel();
        Recordings = new RecordingsViewModel(
            controller.Status,
            controller.StartManualRecordingAsync,
            controller.StopManualRecordingAsync,
            testVideoEncoding,
            controller.ListRecordingsAsync,
            controller.DeleteRecordingAsync,
            recordingDialogs.ConfirmPermanentDelete,
            OpenRecordingsFolderAsync,
            SaveSelectedRecordingCategoryAsync,
            Notifications,
            SavePlaybackAudioStateAsync,
            setRecordingFavorite: controller.SetRecordingFavoriteAsync
        );
        Settings = new SettingsViewModel(
            controller.Status,
            controller.UpdateSettingsAsync,
            settingsDialogs,
            testVideoEncoding: testVideoEncoding,
            windowsStartupShortcut: windowsStartupShortcut,
            initialRecordingStorageStatus: controller.RecordingStorageStatus,
            notifications: Notifications,
            notificationDispatcher: dispatcher
        );
        Diagnostics = new DiagnosticsViewModel(controller.Status, logs, diagnosticsDialogs);
        Updates = new ApplicationUpdateViewModel(
            applicationUpdater,
            dispatcher,
            CanRestartForUpdate,
            requestShutdownForUpdate,
            notifications: Notifications
        );
        NavigationItems =
        [
            new NavigationItemViewModel("Recordings", ShellIconGeometries.Recordings, Recordings),
            new NavigationItemViewModel("Settings", ShellIconGeometries.Settings, Settings),
            new NavigationItemViewModel(
                "Diagnostics",
                ShellIconGeometries.Diagnostics,
                Diagnostics
            ),
        ];
        _selectedNavigationItem = showSettingsOnStartup ? NavigationItems[1] : NavigationItems[0];
        _isSidebarCollapsed = controller.Status.EffectiveSettings?.Ui.SidebarCollapsed ?? false;
        _controller.StatusChanged += OnStatusChanged;
        _controller.RecordingStorageStatusChanged += OnRecordingStorageStatusChanged;
        _logs.LogsChanged += OnLogsChanged;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    public GridLength SidebarWidth =>
        new(IsSidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth);

    public string SidebarToggleToolTip =>
        IsSidebarCollapsed ? "Expand sidebar" : "Collapse sidebar";

    public Geometry SidebarToggleIcon =>
        IsSidebarCollapsed ? ShellIconGeometries.Expand : ShellIconGeometries.Collapse;

    public string AppVersion { get; } = ApplicationVersion.Current;

    public string AppVersionLabel => $"Version {AppVersion}";

    public RecordingsViewModel Recordings { get; }

    public SettingsViewModel Settings { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public NotificationCenterViewModel Notifications { get; }

    public ApplicationUpdateViewModel Updates { get; }

    internal event Action? FavoriteStorageWarningPending;

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _controller.RecordingStorageStatusChanged -= OnRecordingStorageStatusChanged;
        _logs.LogsChanged -= OnLogsChanged;
    }

    public void DiscardPendingSettingsDraftsForExit()
    {
        Settings.DiscardPendingRecordingStorageLimitChange();
    }

    public void StartAutomaticUpdateCheck()
    {
        Updates.StartAutomaticCheck();
    }

    internal FavoriteStorageWarning? TakePendingFavoriteStorageWarning()
    {
        return _favoriteStorageWarnings.TakePendingWarning();
    }

    internal void SelectSettings()
    {
        SelectedNavigationItem = NavigationItems.First(item =>
            ReferenceEquals(item.Content, Settings)
        );
    }

    private bool CanRestartForUpdate()
    {
        return _controller.Status.Recording.State == RecordingCoordinatorState.Idle;
    }

    private bool IsLeavingSettings(NavigationItemViewModel nextNavigationItem)
    {
        return ReferenceEquals(_selectedNavigationItem.Content, Settings)
            && !ReferenceEquals(nextNavigationItem.Content, Settings);
    }

    private Task OpenRecordingsFolderAsync()
    {
        return _controller.OpenRecordingsFolderAsync();
    }

    private async Task SaveSelectedRecordingCategoryAsync(RecordingListCategory category)
    {
        try
        {
            await _controller.UpdateUiSettingsAsync(ui =>
                ui with
                {
                    SelectedRecordingCategory = category,
                }
            );
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The visual preference can safely remain in-memory during shutdown.
        }
    }

    private async Task<bool> SavePlaybackAudioStateAsync(int volumePercent, bool isMuted)
    {
        try
        {
            var result = await _controller.UpdateUiSettingsAsync(ui =>
                ui with
                {
                    PlaybackVolumePercent = volumePercent,
                    IsPlaybackMuted = isMuted,
                }
            );
            return result.WasPersisted;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The playback preference can safely remain in-memory during shutdown.
            return false;
        }
    }

    [RelayCommand]
    private async Task ToggleSidebarAsync()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;

        try
        {
            await _controller.UpdateUiSettingsAsync(ui =>
                ui with
                {
                    SidebarCollapsed = IsSidebarCollapsed,
                }
            );
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The visual preference can safely remain in-memory during shutdown.
        }
    }

    private void OnStatusChanged(ApplicationStatus status)
    {
        _dispatcher.Post(() =>
        {
            Recordings.ApplyStatus(status);
            Settings.ApplyStatus(status);
            Diagnostics.ApplyStatus(status);
            Updates.RefreshCanRestart();
        });
    }

    private void OnRecordingStorageStatusChanged(RecordingStorageStatus status)
    {
        _dispatcher.Post(() =>
        {
            var favoriteStorageWarningPending = _favoriteStorageWarnings.ApplyStatus(status);
            Settings.ApplyRecordingStorageStatus(status);

            if (status.LastDeletedRecordingCount > 0)
            {
                _ = Recordings.RefreshRecordingsAsync();
            }

            if (favoriteStorageWarningPending)
            {
                FavoriteStorageWarningPending?.Invoke();
            }
        });
    }

    private void OnLogsChanged()
    {
        _dispatcher.Post(Diagnostics.RefreshLogs);
    }
}
