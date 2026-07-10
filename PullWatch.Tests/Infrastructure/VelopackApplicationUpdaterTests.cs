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
}
