using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogEventHandler(
    IRecordingService recordingService,
    ILogger<CombatLogEventHandler> logger)
{
    private long _previousRecordingEventTimestamp;

    public async Task HandleAsync(CombatLogEvent combatLogEvent, CancellationToken cancellationToken)
    {
        var eventTimestamp = Stopwatch.GetTimestamp();
        var eventName = combatLogEvent.Name;

        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
            case WowEvents.EncounterStart:
                LogRecordingEventReceived(combatLogEvent, eventTimestamp);
                await recordingService.StartAsync(cancellationToken);
                logger.LogInformation(
                    "Handled {EventName} in {ElapsedMilliseconds:F1} ms",
                    eventName,
                    Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds);
                break;
            case WowEvents.ChallengeModeEnd:
            case WowEvents.EncounterEnd:
                LogRecordingEventReceived(combatLogEvent, eventTimestamp);
                await recordingService.StopAsync(cancellationToken);
                logger.LogInformation(
                    "Handled {EventName} in {ElapsedMilliseconds:F1} ms",
                    eventName,
                    Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds);
                break;
        }
    }

    private void LogRecordingEventReceived(CombatLogEvent combatLogEvent, long eventTimestamp)
    {
        var eventName = combatLogEvent.Name;

        if (_previousRecordingEventTimestamp == 0)
        {
            logger.LogInformation("Received {EventName}", eventName);
        }
        else
        {
            logger.LogInformation(
                "Received {EventName}; previous recording event was {ElapsedSincePreviousEvent}",
                eventName,
                Stopwatch.GetElapsedTime(_previousRecordingEventTimestamp, eventTimestamp));
        }

        _previousRecordingEventTimestamp = eventTimestamp;
        logger.LogInformation("Combat log event: {CombatLogLine}", combatLogEvent.RawLine);
        LogEventMetadata(combatLogEvent);
    }

    private void LogEventMetadata(CombatLogEvent combatLogEvent)
    {
        var arguments = combatLogEvent.Arguments;

        if (combatLogEvent.Name == WowEvents.ChallengeModeStart && arguments.Count >= 5)
        {
            logger.LogInformation(
                "Challenge started: {DungeonName}; instance {InstanceId}, challenge mode {ChallengeModeId}, level {Level}, affixes {Affixes}",
                arguments[0],
                arguments[1],
                arguments[2],
                arguments[3],
                arguments[4]);
            return;
        }

        if (combatLogEvent.Name == WowEvents.EncounterStart && arguments.Count >= 5)
        {
            logger.LogInformation(
                "Encounter started: {EncounterName}; encounter {EncounterId}, difficulty {DifficultyId}, group size {GroupSize}, instance {InstanceId}",
                arguments[1],
                arguments[0],
                arguments[2],
                arguments[3],
                arguments[4]);
            return;
        }

        if (combatLogEvent.Name == WowEvents.EncounterEnd && arguments.Count >= 6)
        {
            logger.LogInformation(
                "Encounter ended: {EncounterName}; encounter {EncounterId}, difficulty {DifficultyId}, group size {GroupSize}, success {Success}, duration {DurationMilliseconds} ms",
                arguments[1],
                arguments[0],
                arguments[2],
                arguments[3],
                arguments[4],
                arguments[5]);
        }
    }
}
