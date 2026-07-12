using Velopack;

namespace PullWatch.Tests;

public sealed class VelopackApplicationUpdaterTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    public void ShouldIncludePrereleaseUpdatesRequiresExplicitOptIn(string? value, bool expected)
    {
        Assert.Equal(expected, VelopackApplicationUpdater.ShouldIncludePrereleaseUpdates(value));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", "1.2.3", true)]
    [InlineData(null, "1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", null, "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3", null, false)]
    [InlineData("1.2.3", "1.2.4", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3", "1.2.4", false)]
    public void CurrentRestartedReleaseRequiresAllVersionsToMatch(
        string? restartedVersion,
        string? currentVersion,
        string? localReleaseVersion,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            VelopackApplicationUpdater.IsCurrentRestartedRelease(
                ParseVersion(restartedVersion),
                ParseVersion(currentVersion),
                ParseVersion(localReleaseVersion)
            )
        );
    }

    private static SemanticVersion? ParseVersion(string? value)
    {
        return value is null ? null : SemanticVersion.Parse(value);
    }
}
