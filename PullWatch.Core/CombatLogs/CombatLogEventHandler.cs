using System.Diagnostics;
using System.Globalization;
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

                if (
                    !CombatLogEventMetadataParser.TryParseChallengeStart(
                        combatLogEvent,
                        receivedAt,
                        out var challengeContext
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Challenge start is missing a dungeon name or valid level"
                    );
                    break;
                }

                await HandleStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    challengeContext,
                    cancellationToken
                );
                break;
            case WowEvents.EncounterStart:
                if (!settingsProvider.Current.RecordRaidEncounters)
                {
                    break;
                }

                if (
                    !CombatLogEventMetadataParser.TryParseEncounterStart(
                        combatLogEvent,
                        receivedAt,
                        out var encounterContext
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Encounter start is missing an encounter id, encounter name, or valid difficulty id"
                    );
                    break;
                }

                await HandleStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    encounterContext,
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
                var hasEncounterIdentity = TryGetEncounterIdentity(
                    combatLogEvent,
                    out var encounterIdentity
                );

                if (!hasEncounterIdentity)
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Encounter end is missing a valid encounter id"
                    );

                    if (
                        recordingCoordinator.Status.Owner
                        is RecordingOwner.ChallengeMode
                            or RecordingOwner.Manual
                    )
                    {
                        break;
                    }
                }

                await HandleEndAsync(
                    combatLogEvent,
                    eventTimestamp,
                    RecordingOwner.Encounter,
                    encounterIdentity,
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

    private static bool TryGetEncounterIdentity(
        CombatLogEvent combatLogEvent,
        out string? encounterIdentity
    )
    {
        encounterIdentity = null;
        var arguments = combatLogEvent.Arguments;

        if (
            arguments.Count == 0
            || !int.TryParse(
                arguments[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var encounterId
            )
        )
        {
            return false;
        }

        encounterIdentity = encounterId.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private void LogEventHandled(string eventName, long eventTimestamp)
    {
        logger.LogInformation(
            "Handled {EventName} in {ElapsedMilliseconds:F1} ms",
            eventName,
            Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds
        );
    }

    private void LogMalformedKnownEvent(CombatLogEvent combatLogEvent, string reason)
    {
        logger.LogWarning(
            "Malformed {EventName}: {Reason}. Combat log event: {CombatLogLine}",
            combatLogEvent.Name,
            reason,
            combatLogEvent.RawLine
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
