using System.Globalization;

namespace PullWatch;

internal static class CombatLogEventMetadataParser
{
    public static ChallengeRecordingContext ParseChallengeStart(
        CombatLogEvent combatLogEvent,
        DateTimeOffset startedAt
    )
    {
        var arguments = combatLogEvent.Arguments;

        return new ChallengeRecordingContext(startedAt, arguments[0], ParseInt(arguments[3]));
    }

    public static EncounterRecordingContext ParseEncounterStart(
        CombatLogEvent combatLogEvent,
        DateTimeOffset startedAt
    )
    {
        var arguments = combatLogEvent.Arguments;

        return new EncounterRecordingContext(
            startedAt,
            ParseInt(arguments[0]),
            arguments[1],
            ParseInt(arguments[2])
        );
    }

    private static int ParseInt(string value)
    {
        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
