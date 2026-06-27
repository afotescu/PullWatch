namespace PullWatch.Tests;

public sealed class WowRaidDifficultyFormatterTests
{
    [Theory]
    [InlineData(WowDifficultyIds.NormalRaid, "Normal")]
    [InlineData(WowDifficultyIds.HeroicRaid, "Heroic")]
    [InlineData(WowDifficultyIds.MythicRaid, "Mythic")]
    [InlineData(WowDifficultyIds.RaidFinder, "Raid Finder")]
    [InlineData(WowDifficultyIds.FlexibleMythicRaid, "Mythic")]
    [InlineData(99, "Difficulty 99")]
    public void FormatsDisplayNames(int difficultyId, string expected)
    {
        Assert.Equal(expected, WowRaidDifficultyFormatter.FormatDisplayName(difficultyId));
    }

    [Theory]
    [InlineData(WowDifficultyIds.NormalRaid, "normal")]
    [InlineData(WowDifficultyIds.HeroicRaid, "heroic")]
    [InlineData(WowDifficultyIds.MythicRaid, "mythic")]
    [InlineData(WowDifficultyIds.RaidFinder, "raid-finder")]
    [InlineData(WowDifficultyIds.FlexibleMythicRaid, "mythic")]
    [InlineData(99, "difficulty-99")]
    public void FormatsFilenameTokens(int difficultyId, string expected)
    {
        Assert.Equal(expected, WowRaidDifficultyFormatter.FormatFilenameToken(difficultyId));
    }
}
