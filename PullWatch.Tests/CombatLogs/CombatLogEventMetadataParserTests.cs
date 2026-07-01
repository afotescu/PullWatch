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
        Assert.Equal(2811, context.MapId);
        Assert.Equal(558, context.ChallengeModeId);
        Assert.Equal(22, context.KeystoneLevel);
        Assert.Equal([9, 10, 147], context.AffixIds);
    }

    [Fact]
    public void ParsesChallengeEnd()
    {
        var endedAt = StartedAt.AddMinutes(31);
        var combatLogEvent = CreateEvent(
            WowEvents.ChallengeModeEnd,
            "2811,1,22,1850000,32.500000,1800.000000"
        );

        var parsed = CombatLogEventMetadataParser.TryParseChallengeEnd(
            combatLogEvent,
            endedAt,
            out var challengeEnd
        );

        Assert.True(parsed);
        Assert.Equal(endedAt, challengeEnd.EndedAt);
        Assert.Equal(2811, challengeEnd.MapId);
        Assert.Equal(ChallengeModeOutcome.Timed, challengeEnd.Outcome);
        Assert.Equal(22, challengeEnd.KeystoneLevel);
        Assert.Equal(1850000, challengeEnd.TotalTimeMilliseconds);
        Assert.Equal(32.5, challengeEnd.OnTimeSeconds);
        Assert.Equal(1800, challengeEnd.MythicRatingAfterRun);
    }

    [Fact]
    public void ParsesChallengeEndWithFractionalTrailingRating()
    {
        var endedAt = StartedAt.AddMinutes(19);
        var combatLogEvent = CreateEvent(
            WowEvents.ChallengeModeEnd,
            "2874,1,12,1153679,380.000000,3512.041992"
        );

        var parsed = CombatLogEventMetadataParser.TryParseChallengeEnd(
            combatLogEvent,
            endedAt,
            out var challengeEnd
        );

        Assert.True(parsed);
        Assert.Equal(ChallengeModeOutcome.Timed, challengeEnd.Outcome);
        Assert.Equal(3512, challengeEnd.MythicRatingAfterRun);
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
    public void ChallengeStartWithMalformedAffixesReturnsFalse()
    {
        var combatLogEvent = CreateEvent(
            WowEvents.ChallengeModeStart,
            "\"Magisters' Terrace\",2811,558,22,not-json"
        );

        var parsed = CombatLogEventMetadataParser.TryParseChallengeStart(
            combatLogEvent,
            StartedAt,
            out _
        );

        Assert.False(parsed);
    }

    [Fact]
    public void ChallengeEndWithInvalidCompletionMetadataReturnsFalse()
    {
        string[] arguments =
        [
            "2811,invalid,22,1850000,32.500000,1800",
            "2811,2,22,1850000,32.500000,1800",
            "2811,1,invalid,1850000,32.500000,1800",
        ];

        foreach (var value in arguments)
        {
            var combatLogEvent = CreateEvent(WowEvents.ChallengeModeEnd, value);
            var parsed = CombatLogEventMetadataParser.TryParseChallengeEnd(
                combatLogEvent,
                StartedAt,
                out _
            );

            Assert.False(parsed);
        }
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
