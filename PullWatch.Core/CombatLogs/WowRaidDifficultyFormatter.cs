namespace PullWatch;

public static class WowRaidDifficultyFormatter
{
    public static string FormatDisplayName(int difficultyId)
    {
        return difficultyId switch
        {
            WowDifficultyIds.NormalRaid => "Normal",
            WowDifficultyIds.HeroicRaid => "Heroic",
            WowDifficultyIds.MythicRaid => "Mythic",
            WowDifficultyIds.RaidFinder => "Raid Finder",
            WowDifficultyIds.FlexibleMythicRaid => "Mythic",
            _ => $"Difficulty {difficultyId}",
        };
    }

    public static string FormatFilenameToken(int difficultyId)
    {
        return difficultyId switch
        {
            WowDifficultyIds.NormalRaid => "normal",
            WowDifficultyIds.HeroicRaid => "heroic",
            WowDifficultyIds.MythicRaid => "mythic",
            WowDifficultyIds.RaidFinder => "raid-finder",
            WowDifficultyIds.FlexibleMythicRaid => "mythic",
            _ => $"difficulty-{difficultyId}",
        };
    }
}
