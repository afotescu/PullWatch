using System.Globalization;

namespace PullWatch;

internal static class CombatLogEventMetadataParser
{
    private const int ChallengeDungeonNameIndex = 0;
    private const int ChallengeLevelIndex = 3;
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
            || arguments.Count <= ChallengeLevelIndex
            || !TryParseInt(arguments[ChallengeLevelIndex], out var level)
        )
        {
            return false;
        }

        context = new ChallengeRecordingContext(
            startedAt,
            arguments[ChallengeDungeonNameIndex],
            level
        );
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

        if (!TryParseInt(arguments[index], out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
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
