using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogReader(
    string logsDirectory,
    ILogger<CombatLogReader> logger)
{
    private const string CombatLogPattern = "WoWCombatLog-*";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task ReadAsync(
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken)
    {
        var combatLogPath = FindLatestCombatLog();
        logger.LogInformation("Reading combat log: {CombatLogPath}", combatLogPath);

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

            logger.LogDebug(line);

            if (CombatLogParser.TryParseEvent(line, out var combatLogEvent))
            {
                await handleEventAsync(combatLogEvent, cancellationToken);
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

}
