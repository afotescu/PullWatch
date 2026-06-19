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

        var parsed = CombatLogEventMetadataParser.TryParseChallengeStart(
            combatLogEvent,
            StartedAt,
            out var context
        );

        Assert.True(parsed);
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

        var parsed = CombatLogEventMetadataParser.TryParseEncounterStart(
            combatLogEvent,
            StartedAt,
            out var context
        );

        Assert.True(parsed);
        Assert.Equal(StartedAt, context.StartedAt);
        Assert.Equal(3129, context.EncounterId);
        Assert.Equal("Plexus Sentinel", context.EncounterName);
        Assert.Equal(16, context.DifficultyId);
    }

    [Fact]
    public void MalformedChallengeStartWithMissingLevelReturnsFalse()
    {
        var combatLogEvent = CreateEvent(
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558"
        );

        var parsed = CombatLogEventMetadataParser.TryParseChallengeStart(
            combatLogEvent,
            StartedAt,
            out _
        );

        Assert.False(parsed);
    }

    [Fact]
    public void ChallengeStartWithNonNumericLevelReturnsFalse()
    {
        var combatLogEvent = CreateEvent(
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558,invalid,[9,10,147]"
        );

        var parsed = CombatLogEventMetadataParser.TryParseChallengeStart(
            combatLogEvent,
            StartedAt,
            out _
        );

        Assert.False(parsed);
    }

    [Fact]
    public void MalformedEncounterStartWithMissingFieldsReturnsFalse()
    {
        var combatLogEvent = CreateEvent(WowEvents.EncounterStart, "3129,\"Plexus Sentinel\"");

        var parsed = CombatLogEventMetadataParser.TryParseEncounterStart(
            combatLogEvent,
            StartedAt,
            out _
        );

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("invalid,\"Plexus Sentinel\",16,20,2810")]
    [InlineData("3129,\"Plexus Sentinel\",invalid,20,2810")]
    public void EncounterStartWithNonNumericIdsReturnsFalse(string arguments)
    {
        var combatLogEvent = CreateEvent(WowEvents.EncounterStart, arguments);

        var parsed = CombatLogEventMetadataParser.TryParseEncounterStart(
            combatLogEvent,
            StartedAt,
            out _
        );

        Assert.False(parsed);
    }

    private static CombatLogEvent CreateEvent(string eventName, string arguments)
    {
        var rawLine = $"{eventName},{arguments}";
        return new CombatLogEvent(eventName, eventName.Length + 1, rawLine);
    }
}
