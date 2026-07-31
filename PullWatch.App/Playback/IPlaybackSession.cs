namespace PullWatch;

internal enum PlaybackOpenFailureKind
{
    Unknown,
    NoMediaDiscovered,
}

internal sealed record PlaybackMediaInfo(
    TimeSpan Duration,
    string VideoCodec,
    int VideoWidth,
    int VideoHeight,
    string PixelFormat,
    double FramesPerSecond,
    bool HardwareAcceleration,
    string AudioCodec,
    int AudioSampleRate,
    int AudioChannels
);

internal sealed record PlaybackOpenResult(
    bool Succeeded,
    string? Error,
    PlaybackOpenFailureKind FailureKind,
    PlaybackMediaInfo? MediaInfo
);

internal readonly record struct PlaybackOpenOperation(long Id, Task<PlaybackOpenResult> Completion);

internal sealed class PlaybackEventArgs(long operationId) : EventArgs
{
    public long OperationId { get; } = operationId;
}

internal sealed class PlaybackFailedEventArgs(long operationId, string? error) : EventArgs
{
    public long OperationId { get; } = operationId;
    public string? Error { get; } = error;
}

internal interface IPlaybackSession : IDisposable
{
    event EventHandler<PlaybackEventArgs>? Ended;
    event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;

    bool CanPlay { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; set; }
    int VolumePercent { get; set; }
    bool IsMuted { get; set; }

    PlaybackOpenOperation BeginOpen(Uri source, CancellationToken cancellationToken);
    void Play();
    void Pause();
    void Stop();
}
