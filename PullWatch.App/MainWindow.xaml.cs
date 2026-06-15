using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;

namespace PullWatch;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _durationTimer;
    private readonly ApplicationLifetimeCoordinator _lifetime;

    public MainWindow(
        ApplicationController controller,
        ApplicationLifetimeCoordinator lifetime,
        InMemoryLogProvider logs)
    {
        InitializeComponent();
        _lifetime = lifetime;
        _viewModel = new MainWindowViewModel(
            controller,
            new WpfUiDispatcher(Dispatcher),
            new WpfSettingsDialogs(),
            logs,
            new WpfDiagnosticsDialogs());
        DataContext = _viewModel;
        _durationTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTimerTick, Dispatcher);
        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!_lifetime.ShouldHideOnWindowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
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
