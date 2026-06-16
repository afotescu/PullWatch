namespace PullWatch.Tests;

public sealed class RecordingPrerequisiteCheckerTests
{
    [Fact]
    public void RejectsUnsupportedWindowsVersion()
    {
        var checker = CreateChecker(isWindowsVersionSupported: false);

        var exception = Assert.Throws<RecordingPrerequisiteException>(
            checker.EnsureSatisfied);

        Assert.Contains("Windows 8 or newer", exception.Message);
    }

    [Fact]
    public void RejectsNon64BitProcess()
    {
        var checker = CreateChecker(is64BitProcess: false);

        var exception = Assert.Throws<RecordingPrerequisiteException>(
            checker.EnsureSatisfied);

        Assert.Contains("64-bit process", exception.Message);
    }

    [Fact]
    public void RejectsMissingVisualCRuntime()
    {
        var checker = CreateChecker(canLoadNativeLibrary: _ => false);

        var exception = Assert.Throws<RecordingPrerequisiteException>(
            checker.EnsureSatisfied);

        Assert.Contains("Visual C++ Redistributable", exception.Message);
        Assert.Contains("restart PullWatch", exception.Message);
    }

    [Fact]
    public void RejectsMissingMediaFoundation()
    {
        var checker = CreateChecker(
            startMediaFoundation: () => throw new DllNotFoundException("mfplat.dll"));

        var exception = Assert.Throws<RecordingPrerequisiteException>(
            checker.EnsureSatisfied);

        Assert.Contains("Media Foundation", exception.Message);
        Assert.Contains("Media Feature Pack", exception.Message);
    }

    [Fact]
    public void RejectsFailedMediaFoundationStartup()
    {
        var checker = CreateChecker(startMediaFoundation: () => unchecked((int)0xC00D36B4));

        var exception = Assert.Throws<RecordingPrerequisiteException>(
            checker.EnsureSatisfied);

        Assert.Contains("0xC00D36B4", exception.Message);
    }

    [Fact]
    public void ShutsDownMediaFoundationAfterSuccessfulStartup()
    {
        var shutdownCount = 0;
        var checker = CreateChecker(stopMediaFoundation: () => shutdownCount++);

        checker.EnsureSatisfied();

        Assert.Equal(1, shutdownCount);
    }

    [Fact]
    public void DescribesNativeDependencyLoadFailure()
    {
        var created = RecordingPrerequisiteException.TryCreateForRecorderStartup(
            new DllNotFoundException("missing native dependency"),
            out var exception);

        Assert.True(created);
        Assert.Contains("native recording dependency", exception.Message);
        Assert.Contains("Visual C++ Redistributable", exception.Message);
    }

    private static RecordingPrerequisiteChecker CreateChecker(
        bool isWindowsVersionSupported = true,
        bool is64BitProcess = true,
        Func<string, bool>? canLoadNativeLibrary = null,
        Func<int>? startMediaFoundation = null,
        Action? stopMediaFoundation = null)
    {
        return new RecordingPrerequisiteChecker(
            () => isWindowsVersionSupported,
            () => is64BitProcess,
            canLoadNativeLibrary ?? (_ => true),
            startMediaFoundation ?? (() => 0),
            stopMediaFoundation ?? (() => { }));
    }
}
