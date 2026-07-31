using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Microsoft.Extensions.Logging;

namespace PullWatch;

internal sealed class FlyleafPlaybackSession : IPlaybackSession
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private long _nextOperationId;
    private PlayerOperation? _activeOperation;
    private int _volumePercent = 50;
    private bool _isMuted;
    private bool _isDisposed;

    public FlyleafPlaybackSession(IUiDispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        FlyleafEngineBootstrapper.Start(logger);
    }

    public event EventHandler<PlaybackEventArgs>? Ended;
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;
    internal event EventHandler? PlayerChanged;

    public bool CanPlay => _activeOperation?.Player.CanPlay ?? false;

    public TimeSpan Duration =>
        _activeOperation?.Player.Duration is > 0 and var duration
            ? TimeSpan.FromTicks(duration)
            : TimeSpan.Zero;

    public TimeSpan Position
    {
        get =>
            _activeOperation?.Player.CurTime is > 0 and var position
                ? TimeSpan.FromTicks(position)
                : TimeSpan.Zero;
        set
        {
            if (_activeOperation is { } operation)
            {
                operation.Player.CurTime = Math.Max(0, value.Ticks);
            }
        }
    }

    public int VolumePercent
    {
        get => _volumePercent;
        set
        {
            _volumePercent = Math.Clamp(value, 0, 100);
            ApplyAudioState();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            ApplyAudioState();
        }
    }

    internal Player? Player => _activeOperation?.Player;

    public PlaybackOpenOperation BeginOpen(Uri source, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        DisposeActivePlayer();

        var operationId = ++_nextOperationId;
        var player = CreatePlayer();
        var completion = new TaskCompletionSource<PlaybackOpenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var operation = new PlayerOperation(operationId, player, completion);
        operation.OpenCompletedHandler = (_, eventArgs) =>
            _dispatcher.Post(() => CompleteOpen(operation, eventArgs));
        operation.OpeningVideoStreamHandler = (_, eventArgs) =>
            _dispatcher.Post(() => LogVideoDecoder(operation, eventArgs));
        operation.PlaybackStoppedHandler = (_, eventArgs) =>
            _dispatcher.Post(() => CompletePlayback(operation, eventArgs));
        player.OpenCompleted += operation.OpenCompletedHandler;
        player.OpeningVideoStream += operation.OpeningVideoStreamHandler;
        player.PlaybackStopped += operation.PlaybackStoppedHandler;
        _activeOperation = operation;
        player.Audio.Volume = _volumePercent;
        player.Audio.Mute = true;
        PlayerChanged?.Invoke(this, EventArgs.Empty);
        var cancellationRegistration = cancellationToken.Register(() =>
            _dispatcher.Post(() => CancelOperation(operation, cancellationToken))
        );
        operation.CancellationRegistration = cancellationRegistration;
        if (!IsCurrent(operation))
        {
            cancellationRegistration.Dispose();
            return new PlaybackOpenOperation(operationId, completion.Task);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new PlaybackOpenOperation(operationId, completion.Task);
        }

        player.OpenAsync(GetPlayerSource(source));
        return new PlaybackOpenOperation(operationId, completion.Task);
    }

    public void Play()
    {
        if (!_isDisposed)
        {
            _activeOperation?.Player.Play();
        }
    }

    public void Pause()
    {
        if (!_isDisposed)
        {
            _activeOperation?.Player.Pause();
        }
    }

    public void Stop()
    {
        if (!_isDisposed)
        {
            DisposeActivePlayer();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DisposeActivePlayer();
    }

    private static Player CreatePlayer()
    {
        var playerConfig = new Config();
        playerConfig.Player.AutoPlay = false;
        playerConfig.Player.Stats = true;
        return new Player(playerConfig);
    }

    private void CompleteOpen(PlayerOperation operation, OpenCompletedArgs eventArgs)
    {
        if (!IsCurrent(operation) || eventArgs.IsSubtitles)
        {
            return;
        }

        var succeeded = eventArgs.Success && string.IsNullOrWhiteSpace(eventArgs.Error);
        if (!succeeded)
        {
            var error = string.IsNullOrWhiteSpace(eventArgs.Error)
                ? "Flyleaf could not open the recording."
                : eventArgs.Error;
            operation.Completion.TrySetResult(
                new PlaybackOpenResult(
                    false,
                    error,
                    RecordingPlayerOpenRetryPolicy.IsMissingPlaylistItemsError(error)
                        ? PlaybackOpenFailureKind.NoMediaDiscovered
                        : PlaybackOpenFailureKind.Unknown,
                    null
                )
            );
            return;
        }

        operation.Completion.TrySetResult(
            new PlaybackOpenResult(
                true,
                null,
                PlaybackOpenFailureKind.Unknown,
                GetMediaInfo(operation.Player)
            )
        );
    }

    private void LogVideoDecoder(PlayerOperation operation, Player.OpeningVideoStreamArgs eventArgs)
    {
        if (!IsCurrent(operation))
        {
            return;
        }

        _logger.LogInformation(
            "Flyleaf selected video decoder; hardwareAcceleration={HardwareAcceleration}",
            eventArgs.VideoAcceleration
        );
    }

    private void CompletePlayback(PlayerOperation operation, PlaybackStoppedArgs eventArgs)
    {
        if (!IsCurrent(operation))
        {
            return;
        }

        if (!eventArgs.Success)
        {
            PlaybackFailed?.Invoke(
                this,
                new PlaybackFailedEventArgs(operation.Id, eventArgs.Error)
            );
            return;
        }

        if (operation.Player.Status == Status.Ended)
        {
            Ended?.Invoke(this, new PlaybackEventArgs(operation.Id));
        }
    }

    private void CancelOperation(PlayerOperation operation, CancellationToken cancellationToken)
    {
        if (!IsCurrent(operation))
        {
            operation.Completion.TrySetCanceled(cancellationToken);
            return;
        }

        operation.Completion.TrySetCanceled(cancellationToken);
        DisposeActivePlayer();
    }

    private void DisposeActivePlayer()
    {
        var operation = _activeOperation;
        if (operation is null)
        {
            return;
        }

        _activeOperation = null;
        operation.CancellationRegistration.Dispose();
        operation.Player.OpenCompleted -= operation.OpenCompletedHandler;
        operation.Player.OpeningVideoStream -= operation.OpeningVideoStreamHandler;
        operation.Player.PlaybackStopped -= operation.PlaybackStoppedHandler;
        PlayerChanged?.Invoke(this, EventArgs.Empty);
        operation.Completion.TrySetCanceled();
        operation.Player.Dispose();
    }

    private void ApplyAudioState()
    {
        if (_activeOperation is not { } operation)
        {
            return;
        }

        operation.Player.Audio.Volume = _volumePercent;
        operation.Player.Audio.Mute = _isMuted || _volumePercent <= 0;
    }

    private bool IsCurrent(PlayerOperation operation)
    {
        return !_isDisposed && ReferenceEquals(_activeOperation, operation);
    }

    private static PlaybackMediaInfo GetMediaInfo(Player player)
    {
        return new PlaybackMediaInfo(
            player.Duration > 0 ? TimeSpan.FromTicks(player.Duration) : TimeSpan.Zero,
            player.Video.Codec?.ToString() ?? string.Empty,
            player.Video.Width,
            player.Video.Height,
            player.Video.PixelFormat?.ToString() ?? string.Empty,
            player.Video.FPS,
            player.Video.VideoAcceleration,
            player.Audio.Codec?.ToString() ?? string.Empty,
            player.Audio.SampleRate,
            player.Audio.Channels
        );
    }

    private static string GetPlayerSource(Uri source)
    {
        return source.IsFile ? source.LocalPath : source.AbsoluteUri;
    }

    private sealed class PlayerOperation(
        long id,
        Player player,
        TaskCompletionSource<PlaybackOpenResult> completion
    )
    {
        public long Id { get; } = id;
        public Player Player { get; } = player;
        public TaskCompletionSource<PlaybackOpenResult> Completion { get; } = completion;
        public EventHandler<OpenCompletedArgs> OpenCompletedHandler { get; set; } = null!;
        public EventHandler<Player.OpeningVideoStreamArgs> OpeningVideoStreamHandler { get; set; } =
            null!;
        public EventHandler<PlaybackStoppedArgs> PlaybackStoppedHandler { get; set; } = null!;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}
