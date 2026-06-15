using System.Windows;
using System.Windows.Threading;

namespace PullWatch;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _durationTimer;

    public MainWindow(ApplicationController controller)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(
            controller,
            new WpfUiDispatcher(Dispatcher),
            new WpfSettingsDialogs());
        DataContext = _viewModel;
        _durationTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTimerTick, Dispatcher);
        Closed += OnClosed;
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        _viewModel.Dashboard.UpdateDuration();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _durationTimer.Stop();
        _viewModel.Dispose();
    }
}
