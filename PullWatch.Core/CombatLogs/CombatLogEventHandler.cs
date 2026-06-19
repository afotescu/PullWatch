using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogEventHandler(
    RecordingCoordinator recordingCoordinator,
    SettingsProvider settingsProvider,
    ILogger<CombatLogEventHandler> logger
)
{
    private long _previousRecordingEventTimestamp;

    public async Task HandleAsync(
        CombatLogEvent combatLogEvent,
        CancellationToken cancellationToken
    )
    {
        var eventTimestamp = Stopwatch.GetTimestamp();
        var receivedAt = DateTimeOffset.Now;
        var eventName = combatLogEvent.Name;

        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
                if (!settingsProvider.Current.RecordMythicPlus)
                {
                    break;
                }

                await HandleStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    CombatLogEventMetadataParser.ParseChallengeStart(combatLogEvent, receivedAt),
                    cancellationToken
                );
                break;
            case WowEvents.EncounterStart:
                if (!settingsProvider.Current.RecordRaidEncounters)
                {
                    break;
                }

                await HandleStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    CombatLogEventMetadataParser.ParseEncounterStart(combatLogEvent, receivedAt),
                    cancellationToken
                );
                break;
            case WowEvents.ChallengeModeEnd:
                await HandleEndAsync(
                    combatLogEvent,
                    eventTimestamp,
                    RecordingOwner.ChallengeMode,
                    null,
                    cancellationToken
                );
                break;
            case WowEvents.EncounterEnd:
                await HandleEndAsync(
                    combatLogEvent,
                    eventTimestamp,
                    RecordingOwner.Encounter,
                    GetEncounterIdentity(combatLogEvent),
                    cancellationToken
                );
                break;
        }
    }

    private async Task HandleStartAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        RecordingContext context,
        CancellationToken cancellationToken
    )
    {
        LogRecordingEventReceived(combatLogEvent, eventTimestamp);
        var result = await recordingCoordinator.StartAutomaticAsync(context, cancellationToken);
        LogCommandResult(combatLogEvent.Name, result);
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
    }

    private async Task HandleEndAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        RecordingOwner owner,
        string? identity,
        CancellationToken cancellationToken
    )
    {
        LogRecordingEventReceived(combatLogEvent, eventTimestamp);
        var result = await recordingCoordinator.StopAutomaticAsync(
            owner,
            identity,
            cancellationToken
        );
        LogCommandResult(combatLogEvent.Name, result);
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
    }

    private void LogCommandResult(string eventName, RecordingCommandResult result)
    {
        logger.LogInformation(
            "Recording coordinator handled {EventName} with result {RecordingCommandResult}",
            eventName,
            result
        );
    }

    private static string? GetEncounterIdentity(CombatLogEvent combatLogEvent)
    {
        return combatLogEvent.Arguments.Count > 0 ? combatLogEvent.Arguments[0] : null;
    }

    private void LogEventHandled(string eventName, long eventTimestamp)
    {
        logger.LogInformation(
            "Handled {EventName} in {ElapsedMilliseconds:F1} ms",
            eventName,
            Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds
        );
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
                Stopwatch.GetElapsedTime(_previousRecordingEventTimestamp, eventTimestamp)
            );
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
                arguments[4]
            );
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
                arguments[4]
            );
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
                arguments[5]
            );
        }
    }
}
