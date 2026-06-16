namespace PullWatch.Tests;

public sealed class WowLogsDirectoryDetectorTests
{
    [Fact]
    public void SkipsDriveCandidatesThatCannotBeInspected()
    {
        var validRoot = Path.GetPathRoot(Environment.CurrentDirectory)
            ?? throw new InvalidOperationException("Current directory has no root.");
        var expected = Path.Combine(validRoot, @"World of Warcraft\_retail_\Logs");

        var result = WowLogsDirectoryDetector.Detect(
            [
                () => throw new IOException("Drive is not ready."),
                () => new WowLogsDriveCandidate(validRoot)
            ],
            path => path == expected);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReturnsNullWhenAllDriveCandidatesCannotBeInspected()
    {
        var result = WowLogsDirectoryDetector.Detect(
            [
                () => throw new IOException("Drive is not ready."),
                () => throw new UnauthorizedAccessException("Drive access denied.")
            ],
            _ => true);

        Assert.Null(result);
    }
}
