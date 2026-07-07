namespace PullWatch.Tests;

public sealed class RecordingFailureClassifierTests
{
    [Fact]
    public void DetectsTargetUnavailableFailures()
    {
        var exception = new InvalidOperationException(
            "outer",
            new CaptureTargetUnavailableException("Could not find target.")
        );

        Assert.True(RecordingFailureClassifier.IsTargetUnavailable(exception));
    }

    [Fact]
    public void DetectsTargetUnavailableFailuresInAggregateExceptions()
    {
        var exception = new AggregateException(
            new InvalidOperationException("first"),
            new CaptureTargetUnavailableException("Could not find target.")
        );

        Assert.True(RecordingFailureClassifier.IsTargetUnavailable(exception));
    }

    [Fact]
    public void IgnoresUnrelatedFailuresForTargetUnavailable()
    {
        var exception = new InvalidOperationException("encoder failed");

        Assert.False(RecordingFailureClassifier.IsTargetUnavailable(exception));
    }

    [Fact]
    public void DetectsOutputUnavailableFailures()
    {
        var exception = new InvalidOperationException(
            "outer",
            new RecordingOutputUnavailableException(
                @"C:\Recordings",
                new IOException("Could not create folder.")
            )
        );

        Assert.True(RecordingFailureClassifier.IsOutputUnavailable(exception));
    }
}
