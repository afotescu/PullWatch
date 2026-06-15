namespace PullWatch;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationController _controller;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISettingsDialogs _settingsDialogs;
    private readonly InMemoryLogProvider _logs;
    private NavigationItemViewModel _selectedNavigationItem;

    public MainWindowViewModel(
        ApplicationController controller,
        IUiDispatcher dispatcher,
        ISettingsDialogs settingsDialogs,
        InMemoryLogProvider logs,
        IDiagnosticsDialogs diagnosticsDialogs)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _settingsDialogs = settingsDialogs;
        _logs = logs;
        Dashboard = new DashboardViewModel(
            controller.Status,
            controller.StartManualRecordingAsync,
            controller.StopManualRecordingAsync,
            OpenRecordingsFolderAsync);
        Settings = new SettingsViewModel(
            controller.Status,
            controller.SaveSettingsAsync,
            settingsDialogs);
        Diagnostics = new DiagnosticsViewModel(controller.Status, logs, diagnosticsDialogs);
        NavigationItems =
        [
            new NavigationItemViewModel("Dashboard", "\uE80F", Dashboard),
            new NavigationItemViewModel("Settings", "\uE713", Settings),
            new NavigationItemViewModel("Diagnostics", "\uE9D9", Diagnostics)
        ];
        _selectedNavigationItem = NavigationItems[0];
        _controller.StatusChanged += OnStatusChanged;
        _logs.LogsChanged += OnLogsChanged;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public DashboardViewModel Dashboard { get; }

    public SettingsViewModel Settings { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public NavigationItemViewModel SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (ReferenceEquals(value, _selectedNavigationItem))
            {
                return;
            }

            if (ReferenceEquals(_selectedNavigationItem.Content, Settings) && Settings.IsDirty)
            {
                if (_settingsDialogs.SaveBeforeLeavingSettings())
                {
                    _ = SaveAndSelectAsync(value);
                    OnPropertyChanged();
                    return;
                }

                Settings.DiscardChanges();
            }

            SetProperty(ref _selectedNavigationItem, value);
        }
    }

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _logs.LogsChanged -= OnLogsChanged;
    }

    private Task OpenRecordingsFolderAsync()
    {
        return _controller.OperatingSystemActions?.OpenRecordingsFolderAsync(CancellationToken.None)
            ?? Task.CompletedTask;
    }

    private void OnStatusChanged(ApplicationStatus status)
    {
        _dispatcher.Post(() =>
        {
            Dashboard.ApplyStatus(status);
            Settings.ApplyStatus(status);
            Diagnostics.ApplyStatus(status);
        });
    }

    private void OnLogsChanged()
    {
        _dispatcher.Post(Diagnostics.RefreshLogs);
    }

    private async Task SaveAndSelectAsync(NavigationItemViewModel navigationItem)
    {
        if (await Settings.SaveChangesAsync())
        {
            SelectedNavigationItem = navigationItem;
        }
    }
}
