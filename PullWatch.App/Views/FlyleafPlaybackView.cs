using FlyleafLib.Controls.WPF;

namespace PullWatch;

public sealed class FlyleafPlaybackView : FlyleafHost
{
    private FlyleafPlaybackSession? _session;
    private bool _isDisposed;

    internal void Attach(IPlaybackSession session)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (session is not FlyleafPlaybackSession flyleafSession)
        {
            throw new ArgumentException(
                "The Flyleaf playback view requires a Flyleaf playback session.",
                nameof(session)
            );
        }

        DetachSession();
        _session = flyleafSession;
        _session.PlayerChanged += OnPlayerChanged;
        UpdatePlayer();
    }

    internal void DetachSession()
    {
        if (_session is not null)
        {
            _session.PlayerChanged -= OnPlayerChanged;
            _session = null;
        }

        Player = null;
    }

    public new void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachSession();
        base.Dispose();
    }

    private void OnPlayerChanged(object? sender, EventArgs eventArgs)
    {
        UpdatePlayer();
    }

    private void UpdatePlayer()
    {
        Player = _session?.Player;
    }
}
