using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;

namespace PullWatch;

public partial class MainWindow : Window
{
    private readonly ApplicationController _controller;
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _durationTimer;
    private readonly ApplicationLifetimeCoordinator _lifetime;
    private bool _placementSaved;

    public MainWindow(
        ApplicationController controller,
        ApplicationLifetimeCoordinator lifetime,
        InMemoryLogProvider logs,
        bool showSettingsOnStartup)
    {
        InitializeComponent();
        _controller = controller;
        _lifetime = lifetime;
        _viewModel = new MainWindowViewModel(
            controller,
            new WpfUiDispatcher(Dispatcher),
            new WpfSettingsDialogs(),
            logs,
            new WpfDiagnosticsDialogs(),
            showSettingsOnStartup);
        DataContext = _viewModel;
        RestoreWindowPlacement(controller.Status.EffectiveSettings?.Ui.WindowPlacement);
        _durationTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTimerTick, Dispatcher);
        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!_lifetime.ShouldHideOnWindowClose)
        {
            SaveWindowPlacement();
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        _viewModel.Recordings.UpdateDuration();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        SaveWindowPlacement();
        _durationTimer.Stop();
        _viewModel.Dispose();
    }

    private void RestoreWindowPlacement(WindowPlacementSettings? placement)
    {
        if (placement?.Left is not { } left ||
            placement.Top is not { } top ||
            placement.Width is not { } width ||
            placement.Height is not { } height ||
            width < MinWidth ||
            height < MinHeight)
        {
            return;
        }

        var bounds = new Rect(left, top, width, height);

        if (!IsVisibleOnCurrentDesktop(bounds))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        if (_placementSaved)
        {
            return;
        }

        _placementSaved = true;

        var restoreBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        var placement = new WindowPlacementSettings
        {
            Left = restoreBounds.Left,
            Top = restoreBounds.Top,
            Width = restoreBounds.Width,
            Height = restoreBounds.Height,
            IsMaximized = WindowState == WindowState.Maximized
        };
        var currentUi = _controller.Status.EffectiveSettings?.Ui ?? new UiSettings();

        try
        {
            Task.Run(
                    () => _controller.SaveUiSettingsAsync(
                        currentUi with { WindowPlacement = placement },
                        CancellationToken.None))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown may already be far enough along that settings are no longer available.
        }
    }

    private static bool IsVisibleOnCurrentDesktop(Rect bounds)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return virtualScreen.IntersectsWith(bounds);
    }
}
