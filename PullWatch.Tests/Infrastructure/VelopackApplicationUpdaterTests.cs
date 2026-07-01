namespace PullWatch.Tests;

public sealed class VelopackApplicationUpdaterTests
{
    [Fact]
    public void ShouldIncludePrereleaseUpdatesAcceptsExplicitOptInValue()
    {
        Assert.True(VelopackApplicationUpdater.ShouldIncludePrereleaseUpdates("1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" yes ")]
    [InlineData("on")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("anything")]
    public void ShouldIncludePrereleaseUpdatesDefaultsToFalse(string? value)
    {
        Assert.False(VelopackApplicationUpdater.ShouldIncludePrereleaseUpdates(value));
    }
}
