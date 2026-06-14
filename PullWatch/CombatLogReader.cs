namespace PullWatch;

public sealed class CombatLogReader(string logsDirectory)
{
    private const string CombatLogPattern = "WoWCombatLog-*";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task ReadAsync(
        Func<string, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken)
    {
        var combatLogPath = FindLatestCombatLog();
        Console.WriteLine($"Reading combat log: {combatLogPath}");

        await using var stream = new FileStream(
            combatLogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        stream.Seek(0, SeekOrigin.End);

        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                await Task.Delay(PollInterval, cancellationToken);
                continue;
            }

            if (TryGetEventName(line, out var eventName))
            {
                await handleEventAsync(eventName, cancellationToken);
            }
        }
    }

    private string FindLatestCombatLog()
    {
        if (!Directory.Exists(logsDirectory))
        {
            throw new DirectoryNotFoundException($"Combat log directory not found: {logsDirectory}");
        }

        var latestCombatLog = new DirectoryInfo(logsDirectory)
            .EnumerateFiles(CombatLogPattern)
            .MaxBy(file => file.LastWriteTimeUtc);

        return latestCombatLog?.FullName
            ?? throw new FileNotFoundException($"No combat log matching '{CombatLogPattern}' was found.");
    }

    private static bool TryGetEventName(string line, out string eventName)
    {
        eventName = string.Empty;

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

        eventName = line[eventStart..eventEnd];
        return eventName.Length > 0;
    }
}
