namespace PullWatch.Tests;

public sealed class RecordingPlayerOpenRetryPolicyTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void RetriesMissingPlaylistItemsForBoundedAttempts(int attempt, bool expected)
    {
        var request = Request(attempt);

        Assert.Equal(
            expected,
            RecordingPlayerOpenRetryPolicy.ShouldRetry(request, "No playlist items were found")
        );
    }

    [Fact]
    public void DoesNotRetryUnrelatedPlaybackFailure()
    {
        Assert.False(
            RecordingPlayerOpenRetryPolicy.ShouldRetry(Request(1), "Invalid data was found")
        );
    }

    [Theory]
    [InlineData(1, 250)]
    [InlineData(2, 750)]
    [InlineData(3, 1500)]
    public void UsesIncreasingRetryDelay(int attempt, int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            RecordingPlayerOpenRetryPolicy.GetDelay(Request(attempt))
        );
    }

    private static RecordingPlayerLoadRequest Request(int attempt)
    {
        return new RecordingPlayerLoadRequest(
            Version: attempt,
            Attempt: attempt,
            Source: new Uri(Path.Combine(Path.GetTempPath(), "recording.mp4"), UriKind.Absolute)
        );
    }
}
