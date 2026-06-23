namespace PullWatch.Tests;

public sealed class CombatLogParserTests
{
    [Fact]
    public void ParsesEventNameAndArguments()
    {
        var line =
            $"6/15/2026 00:15:10.0373  ENCOUNTER_START,3129,\"Plexus Sentinel\",{WowDifficultyIds.NormalRaid},10,2810";

        var parsed = CombatLogParser.TryParseEvent(line, out var combatLogEvent);

        Assert.True(parsed);
        Assert.Equal(WowEvents.EncounterStart, combatLogEvent.Name);
        Assert.Equal(
            ["3129", "Plexus Sentinel", $"{WowDifficultyIds.NormalRaid}", "10", "2810"],
            combatLogEvent.Arguments
        );
        Assert.Equal(line, combatLogEvent.RawLine);
        Assert.NotNull(combatLogEvent.LoggedAt);
        Assert.Equal(
            new DateTime(2026, 6, 15, 0, 15, 10).AddTicks(373000),
            combatLogEvent.LoggedAt.Value.DateTime
        );
    }

    [Fact]
    public void ParsesDungeonEncounterEndMetadata()
    {
        var line =
            $"6/14/2026 23:44:07.3663  ENCOUNTER_END,3071,\"Arcanotron Custos\",{WowDifficultyIds.MythicPlus},5,0,70964";

        CombatLogParser.TryParseEvent(line, out var combatLogEvent);

        Assert.Equal(WowEvents.EncounterEnd, combatLogEvent.Name);
        Assert.Equal(
            ["3071", "Arcanotron Custos", $"{WowDifficultyIds.MythicPlus}", "5", "0", "70964"],
            combatLogEvent.Arguments
        );
    }

    [Fact]
    public void PreservesBracketedAffixListAsOneArgument()
    {
        const string line =
            "6/14/2026 23:37:55.0023  CHALLENGE_MODE_START,\"Magisters' Terrace\",2811,558,22,[9,10,147]";

        CombatLogParser.TryParseEvent(line, out var combatLogEvent);

        Assert.Equal(WowEvents.ChallengeModeStart, combatLogEvent.Name);
        Assert.Equal(
            ["Magisters' Terrace", "2811", "558", "22", "[9,10,147]"],
            combatLogEvent.Arguments
        );
    }

    [Fact]
    public void ParsesChallengeModeEndMetadata()
    {
        const string line =
            "6/14/2026 23:37:54.6663  CHALLENGE_MODE_END,2811,0,0,0,0.000000,0.000000";

        CombatLogParser.TryParseEvent(line, out var combatLogEvent);

        Assert.Equal(WowEvents.ChallengeModeEnd, combatLogEvent.Name);
        Assert.Equal(["2811", "0", "0", "0", "0.000000", "0.000000"], combatLogEvent.Arguments);
    }
}
