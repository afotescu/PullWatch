namespace PullWatch;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationController _controller;
    private readonly IUiDispatcher _dispatcher;
    private NavigationItemViewModel _selectedNavigationItem;

    public MainWindowViewModel(ApplicationController controller, IUiDispatcher dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        Dashboard = new DashboardViewModel(
            controller.Status,
            controller.StartManualRecordingAsync,
            controller.StopManualRecordingAsync,
            OpenRecordingsFolderAsync);
        NavigationItems =
        [
            new NavigationItemViewModel("Dashboard", "\uE80F", Dashboard),
            new NavigationItemViewModel(
                "Settings",
                "\uE713",
                new PlaceholderViewModel(
                    "Settings",
                    "Recording settings will be available in the next build step.")),
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

    public NavigationItemViewModel SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set => SetProperty(ref _selectedNavigationItem, value);
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
        _dispatcher.Post(() => Dashboard.ApplyStatus(status));
    }
}
