using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class PlaybackCoordinatorTests
{
    [Fact]
    public async Task OpenSuccessStartsPausedAtZero()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);
        var source = FileSource("recording.mp4");

        coordinator.RequestSource(source);
        Assert.True(coordinator.StartPendingOpen());
        var operation = session.SingleOpen;
        session.CompleteOpen(operation.Id, SuccessfulOpen(TimeSpan.FromMinutes(2)));
        await operation.Completion;
        await WaitForAsync(() => coordinator.State.HasMedia);

        Assert.Equal(source, coordinator.State.Source);
        Assert.False(coordinator.State.IsOpening);
        Assert.True(coordinator.State.IsOpen);
        Assert.True(coordinator.State.HasMedia);
        Assert.False(coordinator.State.IsPlaying);
        Assert.Equal(TimeSpan.Zero, coordinator.State.Position);
        Assert.Equal(TimeSpan.FromMinutes(2), coordinator.State.Duration);
        Assert.Contains("Pause", session.Calls);
    }

    [Fact]
    public async Task SuccessfulOpenWithoutPlayableMediaIsStillMarkedOpen()
    {
        var session = new FakePlaybackSession { CanPlay = false };
        using var coordinator = CreateCoordinator(session);

        coordinator.RequestSource(FileSource("recording.mp4"));
        coordinator.StartPendingOpen();
        var operation = session.SingleOpen;
        session.CompleteOpen(operation.Id, SuccessfulOpen(TimeSpan.FromMinutes(1)));
        await operation.Completion;
        await WaitForAsync(() => coordinator.State.IsOpen);

        Assert.False(coordinator.State.IsOpening);
        Assert.True(coordinator.State.IsOpen);
        Assert.False(coordinator.State.HasMedia);
    }

    [Fact]
    public async Task ReplacementIgnoresCompletionFromEarlierOpen()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);

        coordinator.RequestSource(FileSource("first.mp4"));
        coordinator.StartPendingOpen();
        var first = session.SingleOpen;

        var secondSource = FileSource("second.mp4");
        coordinator.RequestSource(secondSource);
        coordinator.StartPendingOpen();
        var second = session.Opens[^1];

        session.CompleteOpen(first.Id, SuccessfulOpen(TimeSpan.FromMinutes(1)));
        session.CompleteOpen(second.Id, SuccessfulOpen(TimeSpan.FromMinutes(3)));
        await second.Completion;
        await WaitForAsync(() => coordinator.State.HasMedia);

        Assert.Equal(secondSource, coordinator.State.Source);
        Assert.Equal(TimeSpan.FromMinutes(3), coordinator.State.Duration);
    }

    [Fact]
    public async Task FailedOpenShowsCurrentUserFacingError()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);

        coordinator.RequestSource(FileSource("broken.mp4"));
        coordinator.StartPendingOpen();
        var operation = session.SingleOpen;
        session.CompleteOpen(
            operation.Id,
            new PlaybackOpenResult(
                false,
                "Unsupported input",
                PlaybackOpenFailureKind.Unknown,
                null
            )
        );
        await operation.Completion;
        await WaitForAsync(() => coordinator.State.ErrorText is not null);

        Assert.False(coordinator.State.HasMedia);
        Assert.False(coordinator.State.IsOpening);
        Assert.False(coordinator.State.IsOpen);
        Assert.Equal(
            "This recording could not be played: Unsupported input",
            coordinator.State.ErrorText
        );
    }

    [Fact]
    public void PlaybackFailureWhileOpeningIsIgnored()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);

        coordinator.RequestSource(FileSource("recording.mp4"));
        coordinator.StartPendingOpen();
        var operation = session.SingleOpen;

        session.RaisePlaybackFailed(operation.Id, "stop during replacement");

        Assert.Null(coordinator.State.ErrorText);
        Assert.NotNull(coordinator.State.Source);
        Assert.True(coordinator.State.IsOpening);
    }

    [Fact]
    public void SynchronousOpenFailureIsPresentedWithoutEscapingTheCoordinator()
    {
        var session = new FakePlaybackSession
        {
            BeginOpenException = new InvalidOperationException("Player initialization failed"),
        };
        using var coordinator = CreateCoordinator(session);

        coordinator.RequestSource(FileSource("recording.mp4"));

        Assert.True(coordinator.StartPendingOpen());
        Assert.False(coordinator.State.IsOpening);
        Assert.False(coordinator.State.IsOpen);
        Assert.Equal(
            "This recording could not be played: Player initialization failed",
            coordinator.State.ErrorText
        );
    }

    [Fact]
    public async Task PlayAndPauseUpdateStateAndSession()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);
        await OpenAsync(coordinator, session, TimeSpan.FromMinutes(1));

        Assert.True(coordinator.TogglePlayback());
        Assert.True(coordinator.State.IsPlaying);
        Assert.Equal("Play", session.Calls[^1]);

        Assert.True(coordinator.TogglePlayback());
        Assert.False(coordinator.State.IsPlaying);
        Assert.Equal("Pause", session.Calls[^1]);
    }

    [Fact]
    public async Task EndedPlaybackReplaysFromZero()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);
        var operation = await OpenAsync(coordinator, session, TimeSpan.FromMinutes(2));

        Assert.True(coordinator.TogglePlayback());
        session.RaiseEnded(operation.Id);
        Assert.True(coordinator.State.HasEnded);
        Assert.Equal(coordinator.State.Duration, coordinator.State.Position);

        Assert.True(coordinator.TogglePlayback());

        Assert.False(coordinator.State.HasEnded);
        Assert.True(coordinator.State.IsPlaying);
        Assert.Equal(TimeSpan.Zero, session.Position);
    }

    [Fact]
    public async Task SeekToEndUsesInsetUntilPlaybackHasEnded()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);
        await OpenAsync(coordinator, session, TimeSpan.FromSeconds(10));

        Assert.True(coordinator.SeekTo(TimeSpan.FromSeconds(10)));

        Assert.Equal(TimeSpan.FromMilliseconds(9950), session.Position);
        Assert.Equal(TimeSpan.FromSeconds(10), coordinator.State.Position);
    }

    [Fact]
    public async Task StalePlaybackEventsDoNotChangeCurrentState()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);
        var first = await OpenAsync(coordinator, session, TimeSpan.FromMinutes(1));
        var secondSource = FileSource("second.mp4");

        coordinator.RequestSource(secondSource);
        coordinator.StartPendingOpen();
        var second = session.Opens[^1];
        session.CompleteOpen(second.Id, SuccessfulOpen(TimeSpan.FromMinutes(2)));
        await second.Completion;
        await WaitForAsync(() =>
            coordinator.State.Source == secondSource && coordinator.State.HasMedia
        );

        session.RaiseEnded(first.Id);
        session.RaisePlaybackFailed(first.Id, "stale failure");

        Assert.False(coordinator.State.HasEnded);
        Assert.Null(coordinator.State.ErrorText);
    }

    [Fact]
    public async Task DisposalCancelsOutstandingOpenAndIgnoresCallbacks()
    {
        var session = new FakePlaybackSession();
        var coordinator = CreateCoordinator(session);
        coordinator.RequestSource(FileSource("recording.mp4"));
        coordinator.StartPendingOpen();
        var operation = session.SingleOpen;

        coordinator.Dispose();
        session.CompleteOpen(operation.Id, SuccessfulOpen(TimeSpan.FromMinutes(1)));
        session.RaiseEnded(operation.Id);
        await Task.Yield();

        Assert.False(coordinator.State.HasMedia);
        Assert.False(coordinator.State.HasEnded);
        Assert.True(session.OpenTokens[operation.Id].IsCancellationRequested);
    }

    [Fact]
    public async Task NoMediaFailureRetriesWithNextAttempt()
    {
        var session = new FakePlaybackSession();
        var delays = new List<TimeSpan>();
        using var coordinator = new PlaybackCoordinator(
            session,
            new ImmediateUiDispatcher(),
            NullLogger.Instance,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            }
        );
        coordinator.RequestSource(FileSource("recording.mp4"));
        coordinator.StartPendingOpen();
        var first = session.SingleOpen;

        session.CompleteOpen(
            first.Id,
            new PlaybackOpenResult(
                false,
                "No playlist items were found",
                PlaybackOpenFailureKind.NoMediaDiscovered,
                null
            )
        );
        await first.Completion;
        await WaitForAsync(() => session.Opens.Count == 2);

        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
    }

    [Fact]
    public void VolumeAndMuteAreAppliedToSession()
    {
        var session = new FakePlaybackSession();
        using var coordinator = CreateCoordinator(session);

        coordinator.ApplyAudioState(65, isMuted: false);
        coordinator.ToggleMute();

        Assert.Equal(65, session.VolumePercent);
        Assert.True(session.IsMuted);

        coordinator.ToggleMute();

        Assert.Equal(65, session.VolumePercent);
        Assert.False(session.IsMuted);
    }

    private static PlaybackCoordinator CreateCoordinator(FakePlaybackSession session)
    {
        return new PlaybackCoordinator(session, new ImmediateUiDispatcher(), NullLogger.Instance);
    }

    private static async Task<PlaybackOpenOperation> OpenAsync(
        PlaybackCoordinator coordinator,
        FakePlaybackSession session,
        TimeSpan duration
    )
    {
        coordinator.RequestSource(FileSource("recording.mp4"));
        coordinator.StartPendingOpen();
        var operation = session.Opens[^1];
        session.CompleteOpen(operation.Id, SuccessfulOpen(duration));
        await operation.Completion;
        await WaitForAsync(() => coordinator.State.HasMedia);
        return operation;
    }

    private static PlaybackOpenResult SuccessfulOpen(TimeSpan duration)
    {
        return new PlaybackOpenResult(
            true,
            null,
            PlaybackOpenFailureKind.Unknown,
            new PlaybackMediaInfo(
                duration,
                "h264",
                1920,
                1080,
                "yuv420p",
                60,
                true,
                "aac",
                48000,
                2
            )
        );
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }

    private static Uri FileSource(string fileName)
    {
        return new Uri(Path.Combine(Path.GetTempPath(), fileName), UriKind.Absolute);
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
            action();
        }
    }

    private sealed class FakePlaybackSession : IPlaybackSession
    {
        private readonly Dictionary<long, TaskCompletionSource<PlaybackOpenResult>> _completions =
        [];
        private long _nextOperationId;

        public event EventHandler<PlaybackEventArgs>? Ended;
        public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;

        public bool CanPlay { get; set; } = true;
        public TimeSpan Duration { get; set; }
        public TimeSpan Position { get; set; }
        public int VolumePercent { get; set; }
        public bool IsMuted { get; set; }
        public List<string> Calls { get; } = [];
        public List<PlaybackOpenOperation> Opens { get; } = [];
        public Dictionary<long, CancellationToken> OpenTokens { get; } = [];
        public Exception? BeginOpenException { get; init; }
        public PlaybackOpenOperation SingleOpen => Assert.Single(Opens);

        public PlaybackOpenOperation BeginOpen(Uri source, CancellationToken cancellationToken)
        {
            if (BeginOpenException is not null)
            {
                throw BeginOpenException;
            }

            var id = ++_nextOperationId;
            var completion = new TaskCompletionSource<PlaybackOpenResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _completions.Add(id, completion);
            OpenTokens.Add(id, cancellationToken);
            var operation = new PlaybackOpenOperation(id, completion.Task);
            Opens.Add(operation);
            Calls.Add($"Open:{source}");
            return operation;
        }

        public void CompleteOpen(long operationId, PlaybackOpenResult result)
        {
            if (result.Succeeded && result.MediaInfo is { } media)
            {
                Duration = media.Duration;
            }

            _completions[operationId].TrySetResult(result);
        }

        public void RaiseEnded(long operationId)
        {
            Ended?.Invoke(this, new PlaybackEventArgs(operationId));
        }

        public void RaisePlaybackFailed(long operationId, string error)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(operationId, error));
        }

        public void Play()
        {
            Calls.Add("Play");
        }

        public void Pause()
        {
            Calls.Add("Pause");
        }

        public void Stop()
        {
            Calls.Add("Stop");
        }

        public void Dispose()
        {
            Calls.Add("Dispose");
        }
    }
}
