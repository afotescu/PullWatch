using System.Globalization;

namespace PullWatch;

internal static class CombatLogEventMetadataParser
{
    private const int ChallengeDungeonNameIndex = 0;
    private const int ChallengeLevelIndex = 3;
    private const int EncounterIdIndex = 0;
    private const int EncounterNameIndex = 1;
    private const int EncounterDifficultyIdIndex = 2;

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

        context = new EncounterRecordingContext(
            startedAt,
            encounterId,
            arguments[EncounterNameIndex],
            difficultyId
        );
        return true;
    }

    private static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}
