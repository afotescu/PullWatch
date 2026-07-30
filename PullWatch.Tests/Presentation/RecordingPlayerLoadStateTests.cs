namespace PullWatch.Tests;

public sealed class RecordingPlayerLoadStateTests
{
    [Fact]
    public void DuplicateRequestsForSameSourceProduceOneOpen()
    {
        var state = new RecordingPlayerLoadState();
        var source = FileSource("recording.mp4");

        var request = Assert.IsType<RecordingPlayerLoadRequest>(state.Request(source));

        Assert.Equal(1, request.Attempt);
        Assert.Null(state.Request(new Uri(source.LocalPath, UriKind.Absolute)));
        Assert.True(state.TryStart(request));
        Assert.Null(state.Request(new Uri(source.LocalPath, UriKind.Absolute)));
        Assert.True(state.TryComplete(request, succeeded: true));
        Assert.Null(state.Request(new Uri(source.LocalPath, UriKind.Absolute)));
        Assert.Equal(RecordingPlayerLoadStatus.Open, state.Status);
    }

    [Fact]
    public void NewSourceSupersedesPendingOpenAndIgnoresItsCompletion()
    {
        var state = new RecordingPlayerLoadState();
        var firstRequest = Assert.IsType<RecordingPlayerLoadRequest>(
            state.Request(FileSource("first.mp4"))
        );
        Assert.True(state.TryStart(firstRequest));

        var secondRequest = Assert.IsType<RecordingPlayerLoadRequest>(
            state.Request(FileSource("second.mp4"))
        );

        Assert.False(state.TryComplete(firstRequest, succeeded: true));
        Assert.True(state.TryStart(secondRequest));
        Assert.True(state.TryComplete(secondRequest, succeeded: true));
        Assert.Equal(RecordingPlayerLoadStatus.Open, state.Status);
    }

    [Fact]
    public void ClearingSourceInvalidatesPendingOpen()
    {
        var state = new RecordingPlayerLoadState();
        var request = Assert.IsType<RecordingPlayerLoadRequest>(
            state.Request(FileSource("recording.mp4"))
        );
        Assert.True(state.TryStart(request));

        state.Clear();

        Assert.False(state.TryComplete(request, succeeded: true));
        Assert.Null(state.PendingRequest);
        Assert.Equal(RecordingPlayerLoadStatus.Idle, state.Status);
    }

    [Fact]
    public void FailedSourceRequiresExplicitRetry()
    {
        var state = new RecordingPlayerLoadState();
        var source = FileSource("recording.mp4");
        var request = Assert.IsType<RecordingPlayerLoadRequest>(state.Request(source));
        Assert.True(state.TryStart(request));
        Assert.True(state.TryComplete(request, succeeded: false));

        Assert.Null(state.Request(source));

        var retry = Assert.IsType<RecordingPlayerLoadRequest>(
            state.Request(source, retryFailed: true)
        );
        Assert.Equal(2, retry.Attempt);
        Assert.True(state.TryStart(retry));
    }

    private static Uri FileSource(string fileName)
    {
        return new Uri(Path.Combine(Path.GetTempPath(), fileName), UriKind.Absolute);
    }
}
