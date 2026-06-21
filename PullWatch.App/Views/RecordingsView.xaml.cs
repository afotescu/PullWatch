using System.Windows;

namespace PullWatch;

public partial class RecordingsView : UserControl
{
    private FullscreenPlayerWindow? _fullscreenWindow;
    private object _playerDataContextBeforeFullScreen = DependencyProperty.UnsetValue;

    public RecordingsView()
    {
        InitializeComponent();
        RecordingPlayer.FullScreenRequested += OnPlayerFullScreenRequested;
        RecordingPlayer.ExitFullScreenRequested += OnPlayerExitFullScreenRequested;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        CloseFullScreenWindow();
        RecordingPlayer.StopPlayback();
    }

    private void OnPlayerFullScreenRequested(object? sender, EventArgs eventArgs)
    {
        if (_fullscreenWindow is not null || RecordingPlayer.Source is null)
        {
            return;
        }

        _playerDataContextBeforeFullScreen = RecordingPlayer.ReadLocalValue(
            FrameworkElement.DataContextProperty
        );
        RecordingPlayer.DataContext = DataContext;
        PlayerHost.Content = null;

        var fullscreenWindow = new FullscreenPlayerWindow
        {
            Owner = Window.GetWindow(this),
            DataContext = DataContext,
        };
        fullscreenWindow.ExitRequested += OnFullScreenExitRequested;
        fullscreenWindow.Closed += OnFullScreenClosed;
        fullscreenWindow.SetPlayer(RecordingPlayer);

        _fullscreenWindow = fullscreenWindow;
        fullscreenWindow.Show();
        fullscreenWindow.Activate();
    }

    private void OnPlayerExitFullScreenRequested(object? sender, EventArgs eventArgs)
    {
        CloseFullScreenWindow();
    }

    private void OnFullScreenExitRequested(object? sender, EventArgs eventArgs)
    {
        CloseFullScreenWindow();
    }

    private void CloseFullScreenWindow()
    {
        _fullscreenWindow?.Close();
    }

    private void OnFullScreenClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not FullscreenPlayerWindow fullscreenWindow)
        {
            return;
        }

        fullscreenWindow.ExitRequested -= OnFullScreenExitRequested;
        fullscreenWindow.Closed -= OnFullScreenClosed;

        var player = fullscreenWindow.ReleasePlayer();

        if (player is not null)
        {
            PlayerHost.Content = player;
            RestorePlayerDataContext();
        }

        _fullscreenWindow = null;
    }

    private void RestorePlayerDataContext()
    {
        if (_playerDataContextBeforeFullScreen == DependencyProperty.UnsetValue)
        {
            RecordingPlayer.ClearValue(FrameworkElement.DataContextProperty);
        }
        else
        {
            RecordingPlayer.SetValue(
                FrameworkElement.DataContextProperty,
                _playerDataContextBeforeFullScreen
            );
        }

        _playerDataContextBeforeFullScreen = DependencyProperty.UnsetValue;
    }
}
