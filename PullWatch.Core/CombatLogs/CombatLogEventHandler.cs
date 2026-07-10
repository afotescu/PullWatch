using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogEventHandler(
    RecordingCoordinator recordingCoordinator,
    SettingsProvider settingsProvider,
    ILogger<CombatLogEventHandler> logger,
    TimeSpan? challengeWatchdogTimeout = null
) : IAsyncDisposable
{
    private readonly ChallengeModeLifecycle _challengeModeLifecycle = new(
        recordingCoordinator,
        logger,
        challengeWatchdogTimeout
    );
    private readonly EncounterPullCounter _encounterPullCounter = new();
    private long _previousRecordingEventTimestamp;

    public Task HandleAsync(CombatLogEvent combatLogEvent, CancellationToken cancellationToken)
    {
        return _challengeModeLifecycle.HandleEventAsync(
            combatLogEvent,
            (eventTimestamp, occurredAt, eventCancellationToken) =>
                HandleRoutedEventAsync(
                    combatLogEvent,
                    eventTimestamp,
                    occurredAt,
                    eventCancellationToken
                ),
            cancellationToken
        );
    }

    public ValueTask DisposeAsync()
    {
        return _challengeModeLifecycle.DisposeAsync();
    }

    private async Task HandleRoutedEventAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken
    )
    {
        var eventName = combatLogEvent.Name;

        switch (eventName)
        {
            case WowEvents.ChallengeModeStart:
                var mythicPlusSettings = settingsProvider.Current;

                if (!mythicPlusSettings.RecordMythicPlus)
                {
                    break;
                }

                if (
                    !CombatLogEventMetadataParser.TryParseChallengeStart(
                        combatLogEvent,
                        occurredAt,
                        out var challengeContext
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Challenge start is missing a dungeon name, map id, challenge mode id, level, or affixes"
                    );
                    break;
                }

                if (
                    !mythicPlusSettings.RecordingFilters.MythicPlus.Includes(
                        challengeContext.KeystoneLevel
                    )
                )
                {
                    break;
                }

                await HandleChallengeStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    challengeContext,
                    cancellationToken
                );
                break;
            case WowEvents.EncounterStart:
                var raidSettings = settingsProvider.Current;

                if (!raidSettings.RecordRaidEncounters)
                {
                    break;
                }

                if (
                    !CombatLogEventMetadataParser.TryParseEncounterStart(
                        combatLogEvent,
                        occurredAt,
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

                if (
                    !raidSettings.RecordingFilters.RaidEncounters.Includes(
                        encounterContext.DifficultyId
                    )
                )
                {
                    break;
                }

                var numberedEncounterContext = _encounterPullCounter.AssignNextPullNumber(
                    encounterContext
                );
                var encounterStartResult = await HandleStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    numberedEncounterContext,
                    cancellationToken
                );

                if (encounterStartResult == RecordingCommandResult.Started)
                {
                    _encounterPullCounter.Commit(numberedEncounterContext);
                }
                break;
            case WowEvents.ChallengeModeEnd:
                ChallengeRecordingEnd? challengeEnd = null;

                if (
                    !CombatLogEventMetadataParser.TryParseChallengeEnd(
                        combatLogEvent,
                        occurredAt,
                        out challengeEnd
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Challenge end is missing valid completion metadata"
                    );
                }

                await HandleChallengeEndAsync(
                    combatLogEvent,
                    eventTimestamp,
                    challengeEnd,
                    cancellationToken
                );
                break;
            case WowEvents.EncounterEnd:
                var hasEncounterIdentity = TryGetEncounterIdentity(
                    combatLogEvent,
                    out var encounterIdentity
                );
                EncounterRecordingEnd? encounterEnd = null;

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
                else if (
                    !CombatLogEventMetadataParser.TryParseEncounterEnd(
                        combatLogEvent,
                        occurredAt,
                        out encounterEnd
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Encounter end is missing valid encounter completion metadata"
                    );
                }

                await HandleEndAsync(
                    combatLogEvent,
                    eventTimestamp,
                    RecordingOwner.Encounter,
                    encounterIdentity,
                    encounterEnd,
                    cancellationToken
                );
                break;
            case WowEvents.ZoneChange:
                if (
                    !CombatLogEventMetadataParser.TryParseZoneChange(
                        combatLogEvent,
                        occurredAt,
                        out var zoneChange
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Zone change is missing a zone id, zone name, or instance type"
                    );
                    break;
                }

                _challengeModeLifecycle.HandleZoneChange(zoneChange);
                break;
            case WowEvents.MapChange:
                if (
                    !CombatLogEventMetadataParser.TryParseMapChange(
                        combatLogEvent,
                        occurredAt,
                        out var mapChange
                    )
                )
                {
                    LogMalformedKnownEvent(
                        combatLogEvent,
                        "Map change is missing a UI map id or map name"
                    );
                    break;
                }

                _challengeModeLifecycle.HandleMapChange(mapChange);
                break;
        }
    }

    private async Task HandleChallengeStartAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        ChallengeRecordingContext context,
        CancellationToken cancellationToken
    )
    {
        LogRecordingEventReceived(combatLogEvent, eventTimestamp);
        LogEventMetadata(context);
        await _challengeModeLifecycle.HandleStartAsync(
            combatLogEvent.Name,
            context,
            cancellationToken
        );
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
    }

    private async Task HandleChallengeEndAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        ChallengeRecordingEnd? challengeEnd,
        CancellationToken cancellationToken
    )
    {
        LogRecordingEventReceived(combatLogEvent, eventTimestamp);

        if (challengeEnd is not null)
        {
            LogEventMetadata(challengeEnd);
        }

        await _challengeModeLifecycle.HandleEndAsync(
            combatLogEvent.Name,
            challengeEnd,
            cancellationToken
        );
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
    }

    private async Task<RecordingCommandResult> HandleStartAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        RecordingContext context,
        CancellationToken cancellationToken
    )
    {
        LogRecordingEventReceived(combatLogEvent, eventTimestamp);
        LogEventMetadata(context);
        var result = await recordingCoordinator.StartAutomaticAsync(context, cancellationToken);
        LogCommandResult(combatLogEvent.Name, result);
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
        return result;
    }

    private async Task HandleEndAsync(
        CombatLogEvent combatLogEvent,
        long eventTimestamp,
        RecordingOwner owner,
        string? identity,
        RecordingActivityEnd? activityEnd,
        CancellationToken cancellationToken
    )
    {
        LogRecordingEventReceived(combatLogEvent, eventTimestamp);

        if (activityEnd is not null)
        {
            LogEventMetadata(activityEnd);
        }

        var result = await recordingCoordinator.StopAutomaticAsync(
            owner,
            identity,
            activityEnd,
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
    }

    private void LogEventMetadata(RecordingContext context)
    {
        switch (context)
        {
            case ChallengeRecordingContext challenge:
                logger.LogInformation(
                    "Challenge started: {DungeonName}; map {MapId}, challenge mode {ChallengeModeId}, level {KeystoneLevel}, affixes [{AffixIds}]",
                    challenge.DungeonName,
                    challenge.MapId,
                    challenge.ChallengeModeId,
                    challenge.KeystoneLevel,
                    string.Join(',', challenge.AffixIds)
                );
                break;
            case EncounterRecordingContext encounter:
                logger.LogInformation(
                    "Encounter started: {EncounterName}; encounter {EncounterId}, difficulty {DifficultyId}, group size {GroupSize}, instance {InstanceId}",
                    encounter.EncounterName,
                    encounter.EncounterId,
                    encounter.DifficultyId,
                    encounter.GroupSize,
                    encounter.InstanceId
                );
                break;
        }
    }

    private void LogEventMetadata(RecordingActivityEnd activityEnd)
    {
        switch (activityEnd)
        {
            case EncounterRecordingEnd encounter:
                logger.LogInformation(
                    "Encounter ended: {EncounterName}; encounter {EncounterId}, difficulty {DifficultyId}, group size {GroupSize}, outcome {Outcome}, duration {DurationMilliseconds} ms",
                    encounter.EncounterName,
                    encounter.EncounterId,
                    encounter.DifficultyId,
                    encounter.GroupSize,
                    encounter.Outcome,
                    encounter.DurationMilliseconds
                );
                break;
            case ChallengeRecordingEnd challenge:
                logger.LogInformation(
                    "Challenge ended: map {MapId}, outcome {Outcome}, level {KeystoneLevel}, total time {TotalTimeMilliseconds} ms, on-time delta {OnTimeSeconds} s, mythic rating after run {MythicRatingAfterRun}",
                    challenge.MapId,
                    challenge.Outcome,
                    challenge.KeystoneLevel,
                    challenge.TotalTimeMilliseconds,
                    challenge.OnTimeSeconds,
                    challenge.MythicRatingAfterRun
                );
                break;
        }
    }
}
