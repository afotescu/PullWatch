namespace PullWatch.Tests;

public sealed class RecordingFailureClassifierTests
{
    [Fact]
    public void ClassifiesDllLoadFailuresAsVisualCRuntimeFailures()
    {
        var exception = RecordingFailureClassifier.Classify(
            new DllNotFoundException("Unable to load DLL 'ScreenRecorderLib.dll'.")
        );

        Assert.Contains("Visual C++ Redistributable", exception.Message);
        Assert.IsType<DllNotFoundException>(exception.InnerException);
    }

    [Fact]
    public void ClassifiesBadImageFormatAsArchitectureFailure()
    {
        var exception = RecordingFailureClassifier.Classify(
            new BadImageFormatException("Bad image format.")
        );

        Assert.Contains("wrong architecture", exception.Message);
        Assert.IsType<BadImageFormatException>(exception.InnerException);
    }

    [Fact]
    public void ClassifiesMediaFoundationFailures()
    {
        var exception = RecordingFailureClassifier.Classify(
            new InvalidOperationException("Media Foundation encoder failed.")
        );

        Assert.Contains("Windows Media Foundation", exception.Message);
        Assert.Contains("Media Feature Pack", exception.Message);
    }

    [Fact]
    public void ClassifiesMissingMfplatAsMediaFoundationFailure()
    {
        var exception = RecordingFailureClassifier.Classify(
            new DllNotFoundException("Unable to load DLL 'mfplat.dll'.")
        );

        Assert.Contains("Windows Media Foundation", exception.Message);
        Assert.Contains("Media Feature Pack", exception.Message);
    }

    [Fact]
    public void LeavesUnknownFailuresUnchanged()
    {
        var original = new InvalidOperationException("encoder failed");

        var exception = RecordingFailureClassifier.Classify(original);

        Assert.Same(original, exception);
    }
}
