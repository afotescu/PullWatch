namespace PullWatch;

internal static class CombatLogParser
{
    public static bool TryParseEvent(string line, out CombatLogEvent combatLogEvent)
    {
        combatLogEvent = null!;

        var timestampSeparator = line.IndexOf("  ", StringComparison.Ordinal);
        if (timestampSeparator < 0)
        {
            return false;
        }

        var eventStart = timestampSeparator + 2;
        var eventEnd = line.IndexOf(',', eventStart);
        if (eventEnd < 0)
        {
            eventEnd = line.Length;
        }

        var eventName = line[eventStart..eventEnd];
        if (eventName.Length == 0)
        {
            return false;
        }

        var argumentsStart = eventEnd < line.Length ? eventEnd + 1 : line.Length;
        combatLogEvent = new CombatLogEvent(eventName, argumentsStart, line);
        return true;
    }
}
