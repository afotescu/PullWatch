namespace PullWatch;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationController _controller;
    private readonly IUiDispatcher _dispatcher;
    private readonly InMemoryLogProvider _logs;
    private NavigationItemViewModel _selectedNavigationItem;

    public MainWindowViewModel(
        ApplicationController controller,
        IUiDispatcher dispatcher,
        ISettingsDialogs settingsDialogs,
        InMemoryLogProvider logs,
        IDiagnosticsDialogs diagnosticsDialogs,
        IRecordingDialogs recordingDialogs,
        bool showSettingsOnStartup = false
    )
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _logs = logs;
        Recordings = new RecordingsViewModel(
            controller.Status,
            controller.StartManualRecordingAsync,
            controller.StopManualRecordingAsync,
            controller.ListRecordingsAsync,
            controller.DeleteRecordingAsync,
            recordingDialogs.ConfirmPermanentDelete,
            OpenRecordingsFolderAsync
        );
        Settings = new SettingsViewModel(
            controller.Status,
            controller.SaveSettingsAsync,
            settingsDialogs
        );
        Diagnostics = new DiagnosticsViewModel(controller.Status, logs, diagnosticsDialogs);
        NavigationItems =
        [
            new NavigationItemViewModel("Recordings", "\uE80F", Recordings),
            new NavigationItemViewModel("Settings", "\uE713", Settings),
            new NavigationItemViewModel("Diagnostics", "\uE9D9", Diagnostics),
        ];
        _selectedNavigationItem = showSettingsOnStartup ? NavigationItems[1] : NavigationItems[0];
        _controller.StatusChanged += OnStatusChanged;
        _logs.LogsChanged += OnLogsChanged;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public string AppVersionLabel { get; } = $"Version {ApplicationVersion.Current}";

    public RecordingsViewModel Recordings { get; }

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
        return _controller.OpenRecordingsFolderAsync();
    }

    private void OnStatusChanged(ApplicationStatus status)
    {
        _dispatcher.Post(() =>
        {
            Recordings.ApplyStatus(status);
            Settings.ApplyStatus(status);
            Diagnostics.ApplyStatus(status);
        });
    }

    private void OnLogsChanged()
    {
        _dispatcher.Post(Diagnostics.RefreshLogs);
    }
}
