using System.Windows;

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

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

        eventArgs.Handled = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
