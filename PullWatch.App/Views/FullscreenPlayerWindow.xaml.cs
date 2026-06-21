using System.Windows;
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

    private void OnKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        if (eventArgs.Key == WpfKey.Escape)
        {
            eventArgs.Handled = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (
            eventArgs.Handled
            || eventArgs.Key != WpfKey.Space
            || PlayerHost.Content is not RecordingPlayerControl player
        )
        {
            return;
        }

        eventArgs.Handled = player.TogglePlayback();
    }
}
