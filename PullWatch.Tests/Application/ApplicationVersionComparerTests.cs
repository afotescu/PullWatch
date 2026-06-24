namespace PullWatch.Tests;

public sealed class ApplicationVersionComparerTests
{
    [Theory]
    [InlineData("2.0.0", "1.9.0", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("2.0.0", "2.1.0", false)]
    [InlineData("2.0.0", "2.0.0-dev", true)]
    [InlineData("2.0.0-dev", "2.0.0", false)]
    [InlineData("not-a-version", "2.0.0", false)]
    [InlineData("2.0.0", "not-a-version", false)]
    [InlineData(null, "2.0.0", false)]
    public void DetectsOnlyNewerParseableVersions(
        string? candidateVersion,
        string? currentVersion,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ApplicationVersionComparer.IsNewer(candidateVersion, currentVersion)
        );
    }
}
