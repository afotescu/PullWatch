using System.Windows;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PullWatch;

public partial class FullscreenPlayerWindow : Window
{
    public FullscreenPlayerWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? ExitRequested;

    public void SetPlayer(RecordingPlayerControl player)
    {
        PlayerHost.Content = player;
        player.IsFullScreen = true;
    }

    public RecordingPlayerControl? ReleasePlayer()
    {
        var player = PlayerHost.Content as RecordingPlayerControl;
        PlayerHost.Content = null;

        if (player is not null)
        {
            player.IsFullScreen = false;
        }

        return player;
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        if (eventArgs.Key == WpfKey.Escape)
        {
            eventArgs.Handled = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (
            eventArgs.Handled
            || (eventArgs.Key == WpfKey.Space && IsFocusedButton(eventArgs.OriginalSource))
            || PlayerHost.Content is not RecordingPlayerControl player
        )
        {
            return;
        }

        eventArgs.Handled = player.HandlePlaybackKey(eventArgs.Key);
    }

    private static bool IsFocusedButton(object? source)
    {
        for (
            var current = source as DependencyObject;
            current is not null;
            current = GetParent(current)
        )
        {
            if (current is WpfButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        return current is FrameworkElement frameworkElement
            ? frameworkElement.Parent ?? LogicalTreeHelper.GetParent(frameworkElement)
            : LogicalTreeHelper.GetParent(current);
    }
}
