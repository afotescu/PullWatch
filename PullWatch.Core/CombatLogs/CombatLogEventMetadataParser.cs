using System.Globalization;
using System.Text.Json;

namespace PullWatch;

internal static class CombatLogEventMetadataParser
{
    private const int ChallengeDungeonNameIndex = 0;
    private const int ChallengeMapIdIndex = 1;
    private const int ChallengeModeIdIndex = 2;
    private const int ChallengeLevelIndex = 3;
    private const int ChallengeAffixesIndex = 4;
    private const int ChallengeEndMapIdIndex = 0;
    private const int ChallengeEndSuccessIndex = 1;
    private const int ChallengeEndLevelIndex = 2;
    private const int ChallengeEndTotalTimeMillisecondsIndex = 3;
    private const int ChallengeEndOnTimeSecondsIndex = 4;
    private const int ChallengeEndMythicRatingAfterRunIndex = 5;
    private const int ZoneIdIndex = 0;
    private const int ZoneNameIndex = 1;
    private const int ZoneInstanceTypeIndex = 2;
    private const int UiMapIdIndex = 0;
    private const int MapNameIndex = 1;
    private const int EncounterIdIndex = 0;
    private const int EncounterNameIndex = 1;
    private const int EncounterDifficultyIdIndex = 2;
    private const int EncounterGroupSizeIndex = 3;
    private const int EncounterInstanceIdIndex = 4;
    private const int EncounterEndSuccessIndex = 4;
    private const int EncounterEndDurationMillisecondsIndex = 5;

    public static bool TryParseChallengeStart(
        CombatLogEvent combatLogEvent,
        DateTimeOffset startedAt,
        out ChallengeRecordingContext context
    )
    {
        var arguments = combatLogEvent.Arguments;
        context = null!;

        if (
            arguments.Count <= ChallengeDungeonNameIndex
            || arguments.Count <= ChallengeMapIdIndex
            || arguments.Count <= ChallengeModeIdIndex
            || arguments.Count <= ChallengeLevelIndex
            || arguments.Count <= ChallengeAffixesIndex
            || !TryParseInt(arguments[ChallengeMapIdIndex], out var mapId)
            || !TryParseInt(arguments[ChallengeModeIdIndex], out var challengeModeId)
            || !TryParseInt(arguments[ChallengeLevelIndex], out var level)
            || !TryParseIntArray(arguments[ChallengeAffixesIndex], out var affixIds)
        )
        {
            return false;
        }

        context = new ChallengeRecordingContext(
            startedAt,
            arguments[ChallengeDungeonNameIndex],
            mapId,
            challengeModeId,
            level,
            affixIds
        );
        return true;
    }

    public static bool TryParseChallengeEnd(
        CombatLogEvent combatLogEvent,
        DateTimeOffset endedAt,
        out ChallengeRecordingEnd challengeEnd
    )
    {
        var arguments = combatLogEvent.Arguments;
        challengeEnd = null!;

        if (
            arguments.Count <= ChallengeEndMapIdIndex
            || arguments.Count <= ChallengeEndSuccessIndex
            || arguments.Count <= ChallengeEndLevelIndex
            || !TryParseInt(arguments[ChallengeEndMapIdIndex], out var mapId)
            || !TryParseChallengeOutcome(arguments[ChallengeEndSuccessIndex], out var outcome)
            || !TryParseInt(arguments[ChallengeEndLevelIndex], out var level)
            || !TryParseOptionalInt(
                arguments,
                ChallengeEndTotalTimeMillisecondsIndex,
                out var totalTimeMilliseconds
            )
            || !TryParseOptionalDouble(
                arguments,
                ChallengeEndOnTimeSecondsIndex,
                out var onTimeSeconds
            )
            || !TryParseOptionalTruncatedInt(
                arguments,
                ChallengeEndMythicRatingAfterRunIndex,
                out var mythicRatingAfterRun
            )
        )
        {
            return false;
        }

        challengeEnd = new ChallengeRecordingEnd(
            endedAt,
            mapId,
            outcome,
            level,
            totalTimeMilliseconds,
            onTimeSeconds,
            mythicRatingAfterRun
        );
        return true;
    }

    public static bool TryParseZoneChange(
        CombatLogEvent combatLogEvent,
        DateTimeOffset changedAt,
        out ZoneChangeContext context
    )
    {
        var arguments = combatLogEvent.Arguments;
        context = null!;

        if (
            arguments.Count <= ZoneIdIndex
            || arguments.Count <= ZoneNameIndex
            || arguments.Count <= ZoneInstanceTypeIndex
            || !TryParseInt(arguments[ZoneIdIndex], out var zoneId)
            || !TryParseInt(arguments[ZoneInstanceTypeIndex], out var instanceType)
        )
        {
            return false;
        }

        context = new ZoneChangeContext(changedAt, zoneId, arguments[ZoneNameIndex], instanceType);
        return true;
    }

    public static bool TryParseMapChange(
        CombatLogEvent combatLogEvent,
        DateTimeOffset changedAt,
        out MapChangeContext context
    )
    {
        var arguments = combatLogEvent.Arguments;
        context = null!;

        if (
            arguments.Count <= UiMapIdIndex
            || arguments.Count <= MapNameIndex
            || !TryParseInt(arguments[UiMapIdIndex], out var uiMapId)
        )
        {
            return false;
        }

        context = new MapChangeContext(changedAt, uiMapId, arguments[MapNameIndex]);
        return true;
    }

    public static bool TryParseEncounterStart(
        CombatLogEvent combatLogEvent,
        DateTimeOffset startedAt,
        out EncounterRecordingContext context
    )
    {
        var arguments = combatLogEvent.Arguments;
        context = null!;

        if (
            arguments.Count <= EncounterIdIndex
            || arguments.Count <= EncounterNameIndex
            || arguments.Count <= EncounterDifficultyIdIndex
            || !TryParseInt(arguments[EncounterIdIndex], out var encounterId)
            || !TryParseInt(arguments[EncounterDifficultyIdIndex], out var difficultyId)
        )
        {
            return false;
        }

        if (
            !TryParseOptionalInt(arguments, EncounterGroupSizeIndex, out var groupSize)
            || !TryParseOptionalInt(arguments, EncounterInstanceIdIndex, out var instanceId)
        )
        {
            return false;
        }

        context = new EncounterRecordingContext(
            startedAt,
            encounterId,
            arguments[EncounterNameIndex],
            difficultyId,
            groupSize,
            instanceId
        );
        return true;
    }

    public static bool TryParseEncounterEnd(
        CombatLogEvent combatLogEvent,
        DateTimeOffset endedAt,
        out EncounterRecordingEnd encounterEnd
    )
    {
        var arguments = combatLogEvent.Arguments;
        encounterEnd = null!;

        if (
            arguments.Count <= EncounterIdIndex
            || arguments.Count <= EncounterNameIndex
            || arguments.Count <= EncounterDifficultyIdIndex
            || arguments.Count <= EncounterGroupSizeIndex
            || arguments.Count <= EncounterEndSuccessIndex
            || !TryParseInt(arguments[EncounterIdIndex], out var encounterId)
            || !TryParseInt(arguments[EncounterDifficultyIdIndex], out var difficultyId)
            || !TryParseInt(arguments[EncounterGroupSizeIndex], out var groupSize)
            || !TryParseOutcome(arguments[EncounterEndSuccessIndex], out var outcome)
            || !TryParseOptionalInt(
                arguments,
                EncounterEndDurationMillisecondsIndex,
                out var durationMilliseconds
            )
        )
        {
            return false;
        }

        encounterEnd = new EncounterRecordingEnd(
            endedAt,
            encounterId,
            arguments[EncounterNameIndex],
            difficultyId,
            groupSize,
            outcome,
            durationMilliseconds
        );
        return true;
    }

    private static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseOptionalInt(
        IReadOnlyList<string> arguments,
        int index,
        out int? result
    )
    {
        result = null;

        if (arguments.Count <= index)
        {
            return true;
        }

        if (!TryParseFlexibleInt(arguments[index], out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseOptionalDouble(
        IReadOnlyList<string> arguments,
        int index,
        out double? result
    )
    {
        result = null;

        if (arguments.Count <= index)
        {
            return true;
        }

        if (
            !double.TryParse(
                arguments[index],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
        )
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseOptionalTruncatedInt(
        IReadOnlyList<string> arguments,
        int index,
        out int? result
    )
    {
        result = null;

        if (arguments.Count <= index)
        {
            return true;
        }

        if (
            !double.TryParse(
                arguments[index],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed
            )
            || !double.IsFinite(parsed)
            || parsed < int.MinValue
            || parsed > int.MaxValue
        )
        {
            return false;
        }

        result = (int)Math.Truncate(parsed);
        return true;
    }

    private static bool TryParseIntArray(string value, out IReadOnlyList<int> result)
    {
        result = [];

        try
        {
            var parsed = JsonSerializer.Deserialize<int[]>(value);
            if (parsed is null)
            {
                return false;
            }

            result = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseFlexibleInt(string value, out int result)
    {
        if (TryParseInt(value, out result))
        {
            return true;
        }

        if (
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed == Math.Truncate(parsed)
            && parsed >= int.MinValue
            && parsed <= int.MaxValue
        )
        {
            result = (int)parsed;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryParseChallengeOutcome(string value, out ChallengeModeOutcome outcome)
    {
        outcome = ChallengeModeOutcome.Unknown;

        if (!TryParseInt(value, out var parsed))
        {
            return false;
        }

        outcome = parsed switch
        {
            0 => ChallengeModeOutcome.Depleted,
            1 => ChallengeModeOutcome.Timed,
            _ => ChallengeModeOutcome.Unknown,
        };

        return outcome != ChallengeModeOutcome.Unknown;
    }

    private static bool TryParseOutcome(string value, out RaidEncounterOutcome outcome)
    {
        outcome = RaidEncounterOutcome.Unknown;

        if (!TryParseInt(value, out var parsed))
        {
            return false;
        }

        outcome = parsed switch
        {
            0 => RaidEncounterOutcome.Wipe,
            1 => RaidEncounterOutcome.Kill,
            _ => RaidEncounterOutcome.Unknown,
        };

        return outcome != RaidEncounterOutcome.Unknown;
    }
}
