using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PullWatch;

internal sealed record PlaybackCoordinatorState(
    Uri? Source,
    bool IsOpening,
    bool IsOpen,
    bool HasMedia,
    bool IsPlaying,
    bool HasEnded,
    TimeSpan Duration,
    TimeSpan Position,
    int VolumePercent,
    bool IsMuted,
    string? ErrorText
);

internal sealed class PlaybackCoordinator : IDisposable
{
    private const int FallbackUnmuteVolumePercent = 50;
    private static readonly TimeSpan EndSeekInset = TimeSpan.FromMilliseconds(50);
    private static int _nextPlayerInstanceId;

    private readonly IPlaybackSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly RecordingPlayerLoadState _loadState = new();
    private readonly int _playerInstanceId = Interlocked.Increment(ref _nextPlayerInstanceId);
    private CancellationTokenSource? _openCancellation;
    private CancellationTokenSource? _openRetryCancellation;
    private long? _activeOperationId;
    private TimeSpan? _pendingPlaybackStartPosition;
    private int _lastAudibleVolumePercent = FallbackUnmuteVolumePercent;
    private bool _isDisposed;

    public PlaybackCoordinator(
        IPlaybackSession session,
        IUiDispatcher dispatcher,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null
    )
    {
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        _delayAsync = delayAsync ?? Task.Delay;
        State = new PlaybackCoordinatorState(
            null,
            false,
            false,
            false,
            false,
            false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            FallbackUnmuteVolumePercent,
            false,
            null
        );
        _session.Ended += OnSessionEnded;
        _session.PlaybackFailed += OnSessionPlaybackFailed;
        ApplyAudioStateToSession();
    }

    public event EventHandler? StateChanged;

    public PlaybackCoordinatorState State { get; private set; }

    public void RequestSource(Uri? source)
    {
        if (_isDisposed)
        {
            return;
        }

        if (source is null)
        {
            ClearSource();
            return;
        }

        var request = _loadState.Request(source);
        if (request is null)
        {
            _logger.LogDebug(
                "Flyleaf player {PlayerInstanceId} ignored duplicate source {RecordingFile}; state={LoadState}",
                _playerInstanceId,
                GetSourceDisplayName(source),
                _loadState.Status
            );
            return;
        }

        CancelOpen();
        CancelPendingOpenRetry();
        _activeOperationId = null;
        _session.Stop();
        _pendingPlaybackStartPosition = null;
        SetState(
            State with
            {
                Source = source,
                IsOpening = true,
                IsOpen = false,
                HasMedia = false,
                IsPlaying = false,
                HasEnded = false,
                Duration = TimeSpan.Zero,
                Position = TimeSpan.Zero,
                ErrorText = null,
            }
        );
    }

    public bool StartPendingOpen()
    {
        if (_isDisposed || _loadState.PendingRequest is not { } request)
        {
            return false;
        }

        return StartOpen(request);
    }

    public void Stop()
    {
        if (_isDisposed)
        {
            return;
        }

        ClearSource();
    }

    public void Pause()
    {
        if (_isDisposed)
        {
            return;
        }

        if (State.Source is not null && State.HasMedia)
        {
            _session.Pause();
        }

        if (State.IsPlaying)
        {
            SetState(State with { IsPlaying = false });
        }
    }

    public bool TogglePlayback()
    {
        if (_isDisposed || State.Source is null || !State.HasMedia)
        {
            return false;
        }

        if (State.IsPlaying)
        {
            Pause();
            return true;
        }

        var position = State.Position;
        if (State.HasEnded || (State.Duration > TimeSpan.Zero && position >= State.Duration))
        {
            position = Clamp(
                _pendingPlaybackStartPosition ?? TimeSpan.Zero,
                TimeSpan.Zero,
                State.Duration
            );
            _session.Position = position;
        }

        _pendingPlaybackStartPosition = null;
        ApplyAudioStateToSession();
        _session.Play();
        SetState(State with { IsPlaying = true, HasEnded = false, Position = position });
        return true;
    }

    public bool SeekTo(TimeSpan requestedPosition)
    {
        if (_isDisposed || !State.HasMedia || State.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        var position = Clamp(requestedPosition, TimeSpan.Zero, State.Duration);
        if (State.HasEnded)
        {
            _pendingPlaybackStartPosition = position >= State.Duration ? TimeSpan.Zero : position;
            SetState(State with { Position = position });
            return true;
        }

        var sessionPosition =
            position >= State.Duration
                ? Clamp(State.Duration - EndSeekInset, TimeSpan.Zero, State.Duration)
                : position;
        _session.Position = sessionPosition;
        SetState(State with { Position = position });
        return true;
    }

    public bool SeekBy(TimeSpan offset)
    {
        return SeekTo(State.Position + offset);
    }

    public void RefreshPosition()
    {
        if (_isDisposed || !State.HasMedia || State.HasEnded)
        {
            return;
        }

        SetState(State with { Position = Normalize(_session.Position) });
    }

    public void ApplyAudioState(int volumePercent, bool isMuted)
    {
        if (_isDisposed)
        {
            return;
        }

        var volume = Math.Clamp(volumePercent, 0, 100);
        if (volume > 0)
        {
            _lastAudibleVolumePercent = volume;
        }
        else if (_lastAudibleVolumePercent <= 0)
        {
            _lastAudibleVolumePercent = FallbackUnmuteVolumePercent;
        }

        SetAudioState(volume, isMuted || volume <= 0);
    }

    public void SetVolumePercent(int volumePercent, bool unmute)
    {
        if (_isDisposed)
        {
            return;
        }

        var volume = Math.Clamp(volumePercent, 0, 100);
        if (volume > 0)
        {
            _lastAudibleVolumePercent = volume;
        }

        var isMuted = State.IsMuted;
        if (unmute && volume > 0)
        {
            isMuted = false;
        }
        else if (volume <= 0)
        {
            isMuted = true;
        }

        SetAudioState(volume, isMuted);
    }

    public void ToggleMute()
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsEffectivelyMuted())
        {
            SetAudioState(Math.Max(_lastAudibleVolumePercent, 1), false);
            return;
        }

        _lastAudibleVolumePercent = State.VolumePercent;
        SetAudioState(State.VolumePercent, true);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelOpen();
        CancelPendingOpenRetry();
        _activeOperationId = null;
        _pendingPlaybackStartPosition = null;
        _loadState.Clear();
        _session.Ended -= OnSessionEnded;
        _session.PlaybackFailed -= OnSessionPlaybackFailed;
    }

    private void ClearSource()
    {
        CancelOpen();
        CancelPendingOpenRetry();
        var loadVersion = _loadState.Clear();
        _activeOperationId = null;
        _pendingPlaybackStartPosition = null;
        _logger.LogDebug(
            "Flyleaf player {PlayerInstanceId} cleared source at load {LoadVersion}",
            _playerInstanceId,
            loadVersion
        );
        _session.Stop();
        SetState(
            State with
            {
                Source = null,
                IsOpening = false,
                IsOpen = false,
                HasMedia = false,
                IsPlaying = false,
                HasEnded = false,
                Duration = TimeSpan.Zero,
                Position = TimeSpan.Zero,
                ErrorText = null,
            }
        );
    }

    private bool StartOpen(RecordingPlayerLoadRequest request)
    {
        if (!_loadState.TryStart(request))
        {
            return false;
        }

        CancelOpen();
        var cancellation = new CancellationTokenSource();
        _openCancellation = cancellation;
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Flyleaf player {PlayerInstanceId} opening recording {RecordingFile} at load {LoadVersion}, attempt {LoadAttempt}",
            _playerInstanceId,
            GetSourceDisplayName(request.Source),
            request.Version,
            request.Attempt
        );

        try
        {
            var operation = _session.BeginOpen(request.Source, cancellation.Token);
            _activeOperationId = operation.Id;
            _ = ObserveOpenAsync(request, operation, stopwatch, cancellation);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _openCancellation = null;
            cancellation.Dispose();
            if (_loadState.TryComplete(request, succeeded: false))
            {
                var error = string.IsNullOrWhiteSpace(exception.Message)
                    ? "Flyleaf could not open the recording."
                    : exception.Message;
                HandleOpenFailure(
                    request,
                    error,
                    RecordingPlayerOpenRetryPolicy.IsMissingPlaylistItemsError(error)
                        ? PlaybackOpenFailureKind.NoMediaDiscovered
                        : PlaybackOpenFailureKind.Unknown,
                    stopwatch.Elapsed
                );
            }
        }

        return true;
    }

    private async Task ObserveOpenAsync(
        RecordingPlayerLoadRequest request,
        PlaybackOpenOperation operation,
        Stopwatch stopwatch,
        CancellationTokenSource cancellation
    )
    {
        PlaybackOpenResult? result = null;
        try
        {
            result = await operation.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            result = new PlaybackOpenResult(
                false,
                exception.Message,
                PlaybackOpenFailureKind.Unknown,
                null
            );
        }
        finally
        {
            stopwatch.Stop();
        }

        if (result is not null)
        {
            _dispatcher.Post(() =>
                CompleteOpen(request, operation.Id, result, stopwatch.Elapsed, cancellation)
            );
        }
    }

    private void CompleteOpen(
        RecordingPlayerLoadRequest request,
        long operationId,
        PlaybackOpenResult result,
        TimeSpan elapsed,
        CancellationTokenSource cancellation
    )
    {
        if (
            _isDisposed
            || cancellation.IsCancellationRequested
            || !ReferenceEquals(_openCancellation, cancellation)
            || _activeOperationId != operationId
            || !_loadState.IsCurrent(request)
        )
        {
            return;
        }

        _openCancellation = null;
        cancellation.Dispose();
        if (!_loadState.TryComplete(request, result.Succeeded))
        {
            return;
        }

        if (!result.Succeeded)
        {
            var error = string.IsNullOrWhiteSpace(result.Error)
                ? "Flyleaf could not open the recording."
                : result.Error;
            HandleOpenFailure(request, error, result.FailureKind, elapsed);
            return;
        }

        var media = result.MediaInfo;
        var hasMedia = _session.CanPlay;
        var duration = Normalize(media?.Duration ?? _session.Duration);
        _pendingPlaybackStartPosition = null;
        _session.Pause();
        _session.Position = TimeSpan.Zero;
        ApplyAudioStateToSession();
        SetState(
            State with
            {
                HasMedia = hasMedia,
                IsOpening = false,
                IsOpen = true,
                IsPlaying = false,
                HasEnded = false,
                Duration = duration,
                Position = TimeSpan.Zero,
                ErrorText = null,
            }
        );

        if (media is not null)
        {
            _logger.LogInformation(
                "Flyleaf player {PlayerInstanceId} opened {RecordingFile} at load {LoadVersion}, attempt {LoadAttempt} in {ElapsedMilliseconds:F1} ms; duration={Duration}; video={VideoCodec} {VideoWidth}x{VideoHeight} {PixelFormat} {FramesPerSecond:F2} FPS; hardwareAcceleration={HardwareAcceleration}; audio={AudioCodec} {AudioSampleRate} Hz {AudioChannels} channels",
                _playerInstanceId,
                GetSourceDisplayName(request.Source),
                request.Version,
                request.Attempt,
                elapsed.TotalMilliseconds,
                media.Duration,
                media.VideoCodec,
                media.VideoWidth,
                media.VideoHeight,
                media.PixelFormat,
                media.FramesPerSecond,
                media.HardwareAcceleration,
                media.AudioCodec,
                media.AudioSampleRate,
                media.AudioChannels
            );
        }
    }

    private void HandleOpenFailure(
        RecordingPlayerLoadRequest request,
        string error,
        PlaybackOpenFailureKind failureKind,
        TimeSpan elapsed
    )
    {
        if (
            failureKind == PlaybackOpenFailureKind.NoMediaDiscovered
            && RecordingPlayerOpenRetryPolicy.ShouldRetry(request, error)
        )
        {
            ScheduleOpenRetry(request, error);
            return;
        }

        _logger.LogWarning(
            "Flyleaf player {PlayerInstanceId} failed to open {RecordingFile} at load {LoadVersion}, attempt {LoadAttempt} after {ElapsedMilliseconds:F1} ms: {PlaybackError}",
            _playerInstanceId,
            GetSourceDisplayName(request.Source),
            request.Version,
            request.Attempt,
            elapsed.TotalMilliseconds,
            error
        );
        ShowPlaybackError($"This recording could not be played: {error}");
    }

    private void ScheduleOpenRetry(RecordingPlayerLoadRequest request, string error)
    {
        CancelPendingOpenRetry();
        var delay = RecordingPlayerOpenRetryPolicy.GetDelay(request);
        var cancellation = new CancellationTokenSource();
        _openRetryCancellation = cancellation;
        _logger.LogInformation(
            "Flyleaf player {PlayerInstanceId} could not discover media in {RecordingFile} at load {LoadVersion}, attempt {LoadAttempt}; retrying after {RetryDelayMilliseconds} ms: {PlaybackError}",
            _playerInstanceId,
            GetSourceDisplayName(request.Source),
            request.Version,
            request.Attempt,
            delay.TotalMilliseconds,
            error
        );
        _ = RetryOpenAsync(request, delay, cancellation);
    }

    private async Task RetryOpenAsync(
        RecordingPlayerLoadRequest request,
        TimeSpan delay,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            await _delayAsync(delay, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        _dispatcher.Post(() => StartRetry(request, cancellation));
    }

    private void StartRetry(
        RecordingPlayerLoadRequest request,
        CancellationTokenSource cancellation
    )
    {
        if (
            cancellation.IsCancellationRequested
            || _isDisposed
            || !ReferenceEquals(_openRetryCancellation, cancellation)
            || !_loadState.IsCurrent(request)
            || _loadState.Status != RecordingPlayerLoadStatus.Failed
        )
        {
            return;
        }

        _openRetryCancellation = null;
        cancellation.Dispose();
        if (_loadState.Request(request.Source, retryFailed: true) is { } retryRequest)
        {
            StartOpen(retryRequest);
        }
    }

    private void OnSessionEnded(object? sender, PlaybackEventArgs eventArgs)
    {
        if (
            _isDisposed
            || _activeOperationId != eventArgs.OperationId
            || _loadState.Status != RecordingPlayerLoadStatus.Open
        )
        {
            return;
        }

        SetState(State with { IsPlaying = false, HasEnded = true, Position = State.Duration });
    }

    private void OnSessionPlaybackFailed(object? sender, PlaybackFailedEventArgs eventArgs)
    {
        if (_isDisposed || _activeOperationId != eventArgs.OperationId)
        {
            return;
        }

        if (_loadState.Status != RecordingPlayerLoadStatus.Open)
        {
            _logger.LogDebug(
                "Flyleaf player {PlayerInstanceId} ignored playback-stop failure while source load state is {LoadState}: {PlaybackError}",
                _playerInstanceId,
                _loadState.Status,
                eventArgs.Error
            );
            return;
        }

        ShowPlaybackError(
            $"This recording could not be played: {eventArgs.Error ?? "Flyleaf playback failed."}"
        );
    }

    private void ShowPlaybackError(string message)
    {
        _session.Stop();
        SetState(
            State with
            {
                HasMedia = false,
                IsOpening = false,
                IsOpen = false,
                IsPlaying = false,
                HasEnded = false,
                Duration = TimeSpan.Zero,
                Position = TimeSpan.Zero,
                ErrorText = message,
            }
        );
    }

    private void SetAudioState(int volumePercent, bool isMuted)
    {
        ApplyAudioStateToSession(volumePercent, isMuted);
        SetState(State with { VolumePercent = volumePercent, IsMuted = isMuted });
    }

    private void ApplyAudioStateToSession()
    {
        ApplyAudioStateToSession(State.VolumePercent, State.IsMuted);
    }

    private void ApplyAudioStateToSession(int volumePercent, bool isMuted)
    {
        _session.VolumePercent = volumePercent;
        _session.IsMuted = isMuted || volumePercent <= 0;
    }

    private bool IsEffectivelyMuted()
    {
        return State.IsMuted || State.VolumePercent <= 0;
    }

    private void CancelOpen()
    {
        var cancellation = _openCancellation;
        _openCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void CancelPendingOpenRetry()
    {
        var cancellation = _openRetryCancellation;
        _openRetryCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void SetState(PlaybackCoordinatorState state)
    {
        if (state == State)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Normalize(TimeSpan value)
    {
        return value > TimeSpan.Zero ? value : TimeSpan.Zero;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        return value > maximum ? maximum : value;
    }

    private static string GetSourceDisplayName(Uri source)
    {
        return source.IsFile ? Path.GetFileName(source.LocalPath) : source.Host;
    }
}
