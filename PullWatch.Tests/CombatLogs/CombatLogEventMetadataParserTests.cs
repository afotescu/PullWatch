namespace PullWatch.Tests;

public sealed class CombatLogEventMetadataParserTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 6, 15, 0, 15, 10, TimeSpan.Zero);

    [Fact]
    public void ParsesChallengeStart()
    {
        var combatLogEvent = CreateEvent(
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558,22,[9,10,147]"
        );

        var context = CombatLogEventMetadataParser.ParseChallengeStart(combatLogEvent, StartedAt);

        Assert.Equal(StartedAt, context.StartedAt);
        Assert.Equal("Magisters' Terrace", context.DungeonName);
        Assert.Equal(22, context.Level);
    }

    [Fact]
    public void ParsesEncounterStart()
    {
        var combatLogEvent = CreateEvent(
            WowEvents.EncounterStart,
            "3129,\"Plexus Sentinel\",16,20,2810"
        );

        var context = CombatLogEventMetadataParser.ParseEncounterStart(combatLogEvent, StartedAt);

        Assert.Equal(StartedAt, context.StartedAt);
        Assert.Equal(3129, context.EncounterId);
        Assert.Equal("Plexus Sentinel", context.EncounterName);
        Assert.Equal(16, context.DifficultyId);
    }

    private static CombatLogEvent CreateEvent(string eventName, string arguments)
    {
        var rawLine = $"{eventName},{arguments}";
        return new CombatLogEvent(eventName, eventName.Length + 1, rawLine);
    }
}
