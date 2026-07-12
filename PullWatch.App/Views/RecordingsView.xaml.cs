using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfModifierKeys = System.Windows.Input.ModifierKeys;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace PullWatch;

public partial class RecordingsView : UserControl, IDisposable
{
    private MainWindow? _fullScreenHostWindow;
    private Window? _keyboardWindow;
    private object _playerDataContextBeforeFullScreen = DependencyProperty.UnsetValue;
    private bool _isDisposed;

    public RecordingsView()
    {
        InitializeComponent();
        RecordingPlayer.FullScreenRequested += OnPlayerFullScreenRequested;
        RecordingPlayer.ExitFullScreenRequested += OnPlayerExitFullScreenRequested;
        RecordingPlayer.PlaybackAudioStateChanged += OnPlaybackAudioStateChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        if (DataContext is RecordingsViewModel viewModel)
        {
            RecordingPlayer.ApplyPlaybackAudioState(
                viewModel.PlaybackVolumePercent,
                viewModel.IsPlaybackMuted
            );
        }

        AttachWindowKeyHandler();
        Dispatcher.BeginInvoke(FocusPlayerOnViewEntry, DispatcherPriority.Input);
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        DetachWindowKeyHandler();
        SuspendPlayback();
    }

    internal void SuspendPlayback()
    {
        CloseFullScreen();
        RecordingPlayer.SuspendPlayback();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachWindowKeyHandler();
        CloseFullScreen();
        RecordingPlayer.FullScreenRequested -= OnPlayerFullScreenRequested;
        RecordingPlayer.ExitFullScreenRequested -= OnPlayerExitFullScreenRequested;
        RecordingPlayer.PlaybackAudioStateChanged -= OnPlaybackAudioStateChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        RecordingPlayer.DisposePlayback();
    }

    private async void OnPlaybackAudioStateChanged(
        object? sender,
        PlaybackAudioStateChangedEventArgs eventArgs
    )
    {
        if (DataContext is RecordingsViewModel viewModel)
        {
            await viewModel.UpdatePlaybackAudioStateAsync(
                eventArgs.VolumePercent,
                eventArgs.IsMuted
            );
        }
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
            RecordingPlayer.IsFullScreen
            && (eventArgs.Key == WpfKey.Escape || eventArgs.SystemKey == WpfKey.F4)
        )
        {
            eventArgs.Handled = true;
            CloseFullScreen();
            RecordingPlayer.Focus();
            return;
        }

        if (eventArgs.Handled || eventArgs.KeyboardDevice.Modifiers != WpfModifierKeys.None)
        {
            return;
        }

        if (
            eventArgs.Key == WpfKey.Space
            && !ShouldPreserveSpaceInput(eventArgs.OriginalSource)
            && RecordingPlayer.TogglePlayback()
        )
        {
            eventArgs.Handled = true;
            return;
        }

        if (
            (!RecordingPlayer.IsFullScreen && !RecordingPlayer.IsKeyboardFocusWithin)
            || ShouldLeaveShortcutToFocusedControl(eventArgs.OriginalSource, eventArgs.Key)
        )
        {
            return;
        }

        eventArgs.Handled = RecordingPlayer.HandlePlaybackKey(eventArgs.Key);
    }

    private void OnPlayerFullScreenRequested(object? sender, EventArgs eventArgs)
    {
        if (
            _fullScreenHostWindow is not null
            || RecordingPlayer.Source is null
            || Window.GetWindow(this) is not MainWindow hostWindow
        )
        {
            return;
        }

        _playerDataContextBeforeFullScreen = RecordingPlayer.ReadLocalValue(
            FrameworkElement.DataContextProperty
        );
        RecordingPlayer.DataContext = DataContext;
        PlayerHost.Content = null;
        _fullScreenHostWindow = hostWindow;

        if (!hostWindow.EnterFullScreenPlayer(RecordingPlayer, CloseFullScreen))
        {
            _fullScreenHostWindow = null;
            PlayerHost.Content = RecordingPlayer;
            RestorePlayerDataContext();
            return;
        }

        RecordingPlayer.Focus();
    }

    private void OnPlayerExitFullScreenRequested(object? sender, EventArgs eventArgs)
    {
        CloseFullScreen();
        RecordingPlayer.Focus();
    }

    private void CloseFullScreen()
    {
        if (_fullScreenHostWindow is not { } hostWindow)
        {
            return;
        }

        _fullScreenHostWindow = null;
        if (!hostWindow.ExitFullScreenPlayer(RecordingPlayer))
        {
            return;
        }

        PlayerHost.Content = RecordingPlayer;
        RestorePlayerDataContext();
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

            if (
                current is Slider
                && key
                    is WpfKey.Left
                        or WpfKey.Right
                        or WpfKey.Up
                        or WpfKey.Down
                        or WpfKey.PageUp
                        or WpfKey.PageDown
                        or WpfKey.Home
                        or WpfKey.End
            )
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

    private static bool ShouldPreserveSpaceInput(object? source)
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
        }

        return false;
    }

    private void FocusPlayerOnViewEntry()
    {
        if (
            !_isDisposed
            && IsLoaded
            && !IsKeyboardFocusWithin
            && RecordingPlayer.Source is not null
        )
        {
            RecordingPlayer.Focus();
        }
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
