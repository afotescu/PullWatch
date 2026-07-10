using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace PullWatch;

public partial class RecordingsView : UserControl
{
    private FullscreenPlayerWindow? _fullscreenWindow;
    private Window? _keyboardWindow;
    private object _playerDataContextBeforeFullScreen = DependencyProperty.UnsetValue;

    public RecordingsView()
    {
        InitializeComponent();
        RecordingPlayer.FullScreenRequested += OnPlayerFullScreenRequested;
        RecordingPlayer.ExitFullScreenRequested += OnPlayerExitFullScreenRequested;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachWindowKeyHandler();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        DetachWindowKeyHandler();
        CloseFullScreenWindow();
        RecordingPlayer.DisposePlayback();
    }

    private void AttachWindowKeyHandler()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _keyboardWindow))
        {
            return;
        }

        DetachWindowKeyHandler();
        _keyboardWindow = window;
        if (_keyboardWindow is not null)
        {
            _keyboardWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void DetachWindowKeyHandler()
    {
        if (_keyboardWindow is null)
        {
            return;
        }

        _keyboardWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
        _keyboardWindow = null;
    }

    private void OnWindowPreviewKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        if (
            eventArgs.Handled
            || ShouldLeaveShortcutToFocusedControl(eventArgs.OriginalSource, eventArgs.Key)
        )
        {
            return;
        }

        eventArgs.Handled = RecordingPlayer.HandlePlaybackKey(eventArgs.Key);
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

    private static bool ShouldLeaveShortcutToFocusedControl(object? source, WpfKey key)
    {
        for (
            var current = source as DependencyObject;
            current is not null;
            current = GetParent(current)
        )
        {
            if (current is WpfTextBoxBase or WpfPasswordBox or WpfComboBox)
            {
                return true;
            }

            if (key == WpfKey.Space && current is WpfButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is FrameworkElement frameworkElement)
        {
            return frameworkElement.Parent ?? VisualTreeHelper.GetParent(frameworkElement);
        }

        if (current is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        return current is Visual ? VisualTreeHelper.GetParent(current) : null;
    }
}
