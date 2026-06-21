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
            $"3129,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,2810"
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
        Assert.Equal(WowDifficultyIds.MythicRaid, context.DifficultyId);
        Assert.Equal(20, context.GroupSize);
        Assert.Equal(2810, context.InstanceId);
    }

    [Fact]
    public void ParsesEncounterStartWithFlexibleMythicDifficultyId()
    {
        var combatLogEvent = CreateEvent(
            WowEvents.EncounterStart,
            $"3159,\"Rotmire\",{WowDifficultyIds.FlexibleMythicRaid},20,1592"
        );

        var parsed = CombatLogEventMetadataParser.TryParseEncounterStart(
            combatLogEvent,
            StartedAt,
            out var context
        );

        Assert.True(parsed);
        Assert.Equal(3159, context.EncounterId);
        Assert.Equal("Rotmire", context.EncounterName);
        Assert.Equal(WowDifficultyIds.FlexibleMythicRaid, context.DifficultyId);
        Assert.Equal(20, context.GroupSize);
        Assert.Equal(1592, context.InstanceId);
    }

    [Fact]
    public void ParsesEncounterEnd()
    {
        var endedAt = StartedAt.AddMinutes(7);
        var combatLogEvent = CreateEvent(
            WowEvents.EncounterEnd,
            $"3159,\"Rotmire\",{WowDifficultyIds.FlexibleMythicRaid},20,1,466563"
        );

        var parsed = CombatLogEventMetadataParser.TryParseEncounterEnd(
            combatLogEvent,
            endedAt,
            out var encounterEnd
        );

        Assert.True(parsed);
        Assert.Equal(endedAt, encounterEnd.EndedAt);
        Assert.Equal(3159, encounterEnd.EncounterId);
        Assert.Equal("Rotmire", encounterEnd.EncounterName);
        Assert.Equal(WowDifficultyIds.FlexibleMythicRaid, encounterEnd.DifficultyId);
        Assert.Equal(20, encounterEnd.GroupSize);
        Assert.Equal(RaidEncounterOutcome.Kill, encounterEnd.Outcome);
        Assert.Equal(466563, encounterEnd.DurationMilliseconds);
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

    [Fact]
    public void EncounterStartWithNonNumericIdsReturnsFalse()
    {
        string[] arguments =
        [
            $"invalid,\"Plexus Sentinel\",{WowDifficultyIds.MythicRaid},20,2810",
            "3129,\"Plexus Sentinel\",invalid,20,2810",
        ];

        foreach (var value in arguments)
        {
            var combatLogEvent = CreateEvent(WowEvents.EncounterStart, value);
            var parsed = CombatLogEventMetadataParser.TryParseEncounterStart(
                combatLogEvent,
                StartedAt,
                out _
            );

            Assert.False(parsed);
        }
    }

    [Fact]
    public void EncounterEndWithInvalidCompletionMetadataReturnsFalse()
    {
        string[] arguments =
        [
            $"3159,\"Rotmire\",{WowDifficultyIds.FlexibleMythicRaid},20,invalid,466563",
            $"3159,\"Rotmire\",{WowDifficultyIds.FlexibleMythicRaid},20,2,466563",
            $"3159,\"Rotmire\",{WowDifficultyIds.FlexibleMythicRaid}",
        ];

        foreach (var value in arguments)
        {
            var combatLogEvent = CreateEvent(WowEvents.EncounterEnd, value);
            var parsed = CombatLogEventMetadataParser.TryParseEncounterEnd(
                combatLogEvent,
                StartedAt,
                out _
            );

            Assert.False(parsed);
        }
    }

    private static CombatLogEvent CreateEvent(string eventName, string arguments)
    {
        var rawLine = $"{eventName},{arguments}";
        return new CombatLogEvent(eventName, eventName.Length + 1, rawLine);
    }
}
