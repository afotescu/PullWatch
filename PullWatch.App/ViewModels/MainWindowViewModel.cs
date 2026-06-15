namespace PullWatch;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationController _controller;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISettingsDialogs _settingsDialogs;
    private NavigationItemViewModel _selectedNavigationItem;

    public MainWindowViewModel(
        ApplicationController controller,
        IUiDispatcher dispatcher,
        ISettingsDialogs settingsDialogs)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _settingsDialogs = settingsDialogs;
        Dashboard = new DashboardViewModel(
            controller.Status,
            controller.StartManualRecordingAsync,
            controller.StopManualRecordingAsync,
            OpenRecordingsFolderAsync);
        Settings = new SettingsViewModel(
            controller.Status,
            controller.SaveSettingsAsync,
            settingsDialogs);
        NavigationItems =
        [
            new NavigationItemViewModel("Dashboard", "\uE80F", Dashboard),
            new NavigationItemViewModel("Settings", "\uE713", Settings),
            new NavigationItemViewModel(
                "Diagnostics",
                "\uE9D9",
                new PlaceholderViewModel(
                    "Diagnostics",
                    "Detailed application diagnostics will be added after settings."))
        ];
        _selectedNavigationItem = NavigationItems[0];
        _controller.StatusChanged += OnStatusChanged;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public DashboardViewModel Dashboard { get; }

    public SettingsViewModel Settings { get; }

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
        });
    }

    private async Task SaveAndSelectAsync(NavigationItemViewModel navigationItem)
    {
        if (await Settings.SaveChangesAsync())
        {
            SelectedNavigationItem = navigationItem;
        }
    }
}
