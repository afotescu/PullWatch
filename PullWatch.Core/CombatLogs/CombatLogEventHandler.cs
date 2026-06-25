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
    private static readonly TimeSpan DefaultChallengeWatchdogTimeout = TimeSpan.FromSeconds(10);
    private static readonly StringComparer DungeonNameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly TimeSpan _challengeWatchdogTimeout =
        challengeWatchdogTimeout ?? DefaultChallengeWatchdogTimeout;
    private readonly SemaphoreSlim _challengeLifecycleLock = new(1, 1);
    private readonly Dictionary<EncounterPullKey, int> _encounterPullCounts = new();
    private long _previousRecordingEventTimestamp;
    private ChallengeWatchdogState? _challengeWatchdog;
    private CancellationTokenSource? _challengeWatchdogCancellation;
    private Task? _challengeWatchdogTask;
    private bool _disposed;

    public async Task HandleAsync(
        CombatLogEvent combatLogEvent,
        CancellationToken cancellationToken
    )
    {
        await _challengeLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await HandleLockedAsync(combatLogEvent, cancellationToken);
        }
        finally
        {
            _challengeLifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? watchdogTask;
        CancellationTokenSource? watchdogCancellation;

        await _challengeLifecycleLock.WaitAsync(CancellationToken.None);

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watchdogTask = _challengeWatchdogTask;
            watchdogCancellation = _challengeWatchdogCancellation;
            _challengeWatchdog = null;
            _challengeWatchdogTask = null;
            _challengeWatchdogCancellation = null;
            watchdogCancellation?.Cancel();
        }
        finally
        {
            _challengeLifecycleLock.Release();
        }

        await ObserveCanceledTaskAsync(watchdogTask, watchdogCancellation);
    }

    private static async Task ObserveCanceledTaskAsync(
        Task? task,
        CancellationTokenSource? cancellation
    )
    {
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            // Expected during monitor shutdown.
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task HandleLockedAsync(
        CombatLogEvent combatLogEvent,
        CancellationToken cancellationToken
    )
    {
        var eventTimestamp = Stopwatch.GetTimestamp();
        var receivedAt = DateTimeOffset.Now;
        var occurredAt = combatLogEvent.LoggedAt ?? receivedAt;
        var eventName = combatLogEvent.Name;

        if (eventName is not WowEvents.CombatLogVersion and not WowEvents.ChallengeModeEnd)
        {
            await ExpireChallengeWatchdogIfDueAsync(occurredAt, cancellationToken);
            ClearChallengeWatchdogIfRecordingEnded();
        }

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

                var numberedEncounterContext = AssignNextPullNumber(encounterContext);
                var encounterStartResult = await HandleStartAsync(
                    combatLogEvent,
                    eventTimestamp,
                    numberedEncounterContext,
                    cancellationToken
                );

                if (encounterStartResult == RecordingCommandResult.Started)
                {
                    CommitPullNumber(numberedEncounterContext);
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

                HandleZoneChange(zoneChange);
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

                HandleMapChange(mapChange);
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
        LogMythicPlusStart(context);

        if (GetActiveChallengeContext() is { } activeChallenge)
        {
            if (IsSameChallenge(activeChallenge, context))
            {
                LogSameChallengeStartIgnored(activeChallenge, context);
                CancelChallengeWatchdogForRecovery(
                    "SameChallengeStart",
                    activeChallenge,
                    challengeStart: context
                );
                LogCommandResult(combatLogEvent.Name, RecordingCommandResult.AlreadyActive);
                LogEventHandled(combatLogEvent.Name, eventTimestamp);
                return;
            }

            LogDifferentChallengeHardStop(activeChallenge, context);
            ClearChallengeWatchdog();
            var stopResult = await recordingCoordinator.StopAutomaticAsync(
                RecordingOwner.ChallengeMode,
                null,
                null,
                cancellationToken
            );
            LogCommandResult($"{combatLogEvent.Name}:StopDifferentChallenge", stopResult);

            if (stopResult is not RecordingCommandResult.Stopped)
            {
                LogEventHandled(combatLogEvent.Name, eventTimestamp);
                return;
            }
        }

        var result = await recordingCoordinator.StartAutomaticAsync(context, cancellationToken);
        LogCommandResult(combatLogEvent.Name, result);
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
        LogChallengeModeEndHardStop(challengeEnd);
        ClearChallengeWatchdog();
        var result = await recordingCoordinator.StopAutomaticAsync(
            RecordingOwner.ChallengeMode,
            null,
            challengeEnd,
            cancellationToken
        );
        LogCommandResult(combatLogEvent.Name, result);
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
        var result = await recordingCoordinator.StartAutomaticAsync(context, cancellationToken);
        LogCommandResult(combatLogEvent.Name, result);
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
        return result;
    }

    private EncounterRecordingContext AssignNextPullNumber(EncounterRecordingContext context)
    {
        var key = EncounterPullKey.From(context);
        var nextPullNumber = _encounterPullCounts.GetValueOrDefault(key) + 1;
        return context with { PullNumber = nextPullNumber };
    }

    private void CommitPullNumber(EncounterRecordingContext context)
    {
        if (context.PullNumber is not { } pullNumber)
        {
            return;
        }

        _encounterPullCounts[EncounterPullKey.From(context)] = pullNumber;
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
        var result = await recordingCoordinator.StopAutomaticAsync(
            owner,
            identity,
            activityEnd,
            cancellationToken
        );
        LogCommandResult(combatLogEvent.Name, result);
        LogEventHandled(combatLogEvent.Name, eventTimestamp);
    }

    private void HandleZoneChange(ZoneChangeContext zoneChange)
    {
        var activeChallenge = GetActiveChallengeContext();

        if (activeChallenge is null)
        {
            return;
        }

        if (
            zoneChange.ZoneId == activeChallenge.MapId
            && zoneChange.InstanceType == WowDifficultyIds.MythicPlus
        )
        {
            CancelChallengeWatchdogForRecovery(
                "ZoneReturnedToMythicPlus",
                activeChallenge,
                zoneChange: zoneChange
            );
            return;
        }

        if (
            zoneChange.ZoneId != activeChallenge.MapId
            || zoneChange.InstanceType != WowDifficultyIds.MythicPlus
        )
        {
            StartOrRefreshChallengeWatchdog(
                activeChallenge,
                ChallengeSoftStopEvidence.FromZoneChange(zoneChange)
            );
        }
    }

    private void HandleMapChange(MapChangeContext mapChange)
    {
        var activeChallenge = GetActiveChallengeContext();

        if (activeChallenge is null)
        {
            return;
        }

        if (
            DungeonNameComparer.Equals(mapChange.MapName, activeChallenge.DungeonName)
            && !IsNonMythicPlusDungeonZoneSuspected(activeChallenge)
        )
        {
            CancelChallengeWatchdogForRecovery(
                "MapReturnedToDungeon",
                activeChallenge,
                mapChange: mapChange
            );
            return;
        }

        StartOrRefreshChallengeWatchdog(
            activeChallenge,
            ChallengeSoftStopEvidence.FromMapChange(mapChange)
        );
    }

    private bool IsNonMythicPlusDungeonZoneSuspected(ChallengeRecordingContext activeChallenge)
    {
        var evidence = _challengeWatchdog?.Evidence;

        return evidence?.EventName == WowEvents.ZoneChange
            && evidence.ZoneId == activeChallenge.MapId
            && evidence.InstanceType != WowDifficultyIds.MythicPlus;
    }

    private async Task ExpireChallengeWatchdogIfDueAsync(
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken
    )
    {
        var state = _challengeWatchdog;

        if (state is null || occurredAt < state.ExpiresAt)
        {
            return;
        }

        await ExpireChallengeWatchdogAsync(state.ExpiresAt, cancellationToken);
    }

    private async Task ExpireChallengeWatchdogFromTimerAsync(
        ChallengeWatchdogState state,
        CancellationToken cancellationToken
    )
    {
        await _challengeLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            if (!ReferenceEquals(_challengeWatchdog, state))
            {
                return;
            }

            await ExpireChallengeWatchdogAsync(state.ExpiresAt, CancellationToken.None);
        }
        finally
        {
            _challengeLifecycleLock.Release();
        }
    }

    private async Task ExpireChallengeWatchdogAsync(
        DateTimeOffset expiredAt,
        CancellationToken cancellationToken
    )
    {
        var state = ClearChallengeWatchdog();
        var activeChallenge = GetActiveChallengeContext();

        if (
            state is null
            || activeChallenge is null
            || !IsSameChallenge(activeChallenge, state.Challenge)
        )
        {
            return;
        }

        LogChallengeWatchdogExpired(activeChallenge, state, expiredAt);
        var result = await recordingCoordinator.StopAutomaticAsync(
            RecordingOwner.ChallengeMode,
            null,
            CreateInferredDepletedChallengeEnd(activeChallenge, expiredAt),
            cancellationToken
        );
        LogCommandResult("MythicPlusWatchdog", result);
    }

    private void StartOrRefreshChallengeWatchdog(
        ChallengeRecordingContext activeChallenge,
        ChallengeSoftStopEvidence evidence
    )
    {
        ClearChallengeWatchdog();

        var cancellation = new CancellationTokenSource();
        var state = new ChallengeWatchdogState(
            activeChallenge,
            evidence,
            evidence.OccurredAt + _challengeWatchdogTimeout
        );

        _challengeWatchdog = state;
        _challengeWatchdogCancellation = cancellation;
        _challengeWatchdogTask = RunChallengeWatchdogAsync(state, cancellation.Token);
        LogChallengeSoftStopSuspected(activeChallenge, evidence);
    }

    private async Task RunChallengeWatchdogAsync(
        ChallengeWatchdogState state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(_challengeWatchdogTimeout, cancellationToken);
            await ExpireChallengeWatchdogFromTimerAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "M+ watchdog failed");
        }
    }

    private void CancelChallengeWatchdogForRecovery(
        string reason,
        ChallengeRecordingContext activeChallenge,
        ChallengeRecordingContext? challengeStart = null,
        ZoneChangeContext? zoneChange = null,
        MapChangeContext? mapChange = null
    )
    {
        if (ClearChallengeWatchdog() is null)
        {
            return;
        }

        LogChallengeWatchdogCancelled(
            reason,
            activeChallenge,
            challengeStart,
            zoneChange,
            mapChange
        );
    }

    private ChallengeWatchdogState? ClearChallengeWatchdog()
    {
        var state = _challengeWatchdog;
        var cancellation = _challengeWatchdogCancellation;
        var task = _challengeWatchdogTask;

        _challengeWatchdog = null;
        _challengeWatchdogCancellation = null;
        _challengeWatchdogTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
            _ = DisposeChallengeWatchdogCancellationAsync(task, cancellation);
        }

        return state;
    }

    private void ClearChallengeWatchdogIfRecordingEnded()
    {
        var state = _challengeWatchdog;

        if (state is null)
        {
            return;
        }

        var activeChallenge = GetActiveChallengeContext();

        if (activeChallenge is null || !IsSameChallenge(activeChallenge, state.Challenge))
        {
            ClearChallengeWatchdog();
        }
    }

    private static async Task DisposeChallengeWatchdogCancellationAsync(
        Task? task,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            // The watchdog task logs non-cancellation failures before completing.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private ChallengeRecordingContext? GetActiveChallengeContext()
    {
        var status = recordingCoordinator.Status;

        return
            status.Owner == RecordingOwner.ChallengeMode
            && status.State
                is RecordingCoordinatorState.Starting
                    or RecordingCoordinatorState.Recording
            && status.Context is ChallengeRecordingContext challengeContext
            ? challengeContext
            : null;
    }

    private static bool IsSameChallenge(
        ChallengeRecordingContext left,
        ChallengeRecordingContext right
    )
    {
        return left.MapId == right.MapId
            && left.ChallengeModeId == right.ChallengeModeId
            && left.KeystoneLevel == right.KeystoneLevel;
    }

    private static ChallengeRecordingEnd CreateInferredDepletedChallengeEnd(
        ChallengeRecordingContext activeChallenge,
        DateTimeOffset endedAt
    )
    {
        return new ChallengeRecordingEnd(
            endedAt,
            activeChallenge.MapId,
            ChallengeModeOutcome.Depleted,
            activeChallenge.KeystoneLevel,
            null,
            null,
            null
        );
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
            return;
        }

        if (combatLogEvent.Name == WowEvents.ChallengeModeEnd && arguments.Count >= 6)
        {
            logger.LogInformation(
                "Challenge ended: instance {InstanceId}, success {Success}, level {Level}, total time {TotalTimeMilliseconds} ms, on-time delta {OnTimeSeconds} s, timer limit {TimerLimitSeconds} s",
                arguments[0],
                arguments[1],
                arguments[2],
                arguments[3],
                arguments[4],
                arguments[5]
            );
        }
    }

    private void LogMythicPlusStart(ChallengeRecordingContext context)
    {
        logger.LogInformation(
            "M+ start: dungeon={DungeonName} map={MapId} challenge={ChallengeModeId} level={KeystoneLevel} affixes=[{AffixIds}]",
            context.DungeonName,
            context.MapId,
            context.ChallengeModeId,
            context.KeystoneLevel,
            string.Join(",", context.AffixIds)
        );
    }

    private void LogChallengeModeEndHardStop(ChallengeRecordingEnd? challengeEnd)
    {
        if (challengeEnd is null)
        {
            logger.LogInformation("M+ hard stop: reason=ChallengeModeEnd");
            return;
        }

        logger.LogInformation(
            "M+ hard stop: reason=ChallengeModeEnd map={MapId} outcome={Outcome} level={KeystoneLevel} totalTimeMs={TotalTimeMilliseconds}",
            challengeEnd.MapId,
            challengeEnd.Outcome,
            challengeEnd.KeystoneLevel,
            challengeEnd.TotalTimeMilliseconds
        );
    }

    private void LogDifferentChallengeHardStop(
        ChallengeRecordingContext activeChallenge,
        ChallengeRecordingContext nextChallenge
    )
    {
        logger.LogInformation(
            "M+ hard stop: reason=DifferentChallengeStart activeDungeon={ActiveDungeonName} activeMap={ActiveMapId} activeChallenge={ActiveChallengeModeId} activeLevel={ActiveKeystoneLevel} nextDungeon={NextDungeonName} nextMap={NextMapId} nextChallenge={NextChallengeModeId} nextLevel={NextKeystoneLevel}",
            activeChallenge.DungeonName,
            activeChallenge.MapId,
            activeChallenge.ChallengeModeId,
            activeChallenge.KeystoneLevel,
            nextChallenge.DungeonName,
            nextChallenge.MapId,
            nextChallenge.ChallengeModeId,
            nextChallenge.KeystoneLevel
        );
    }

    private void LogSameChallengeStartIgnored(
        ChallengeRecordingContext activeChallenge,
        ChallengeRecordingContext duplicateStart
    )
    {
        logger.LogInformation(
            "M+ same start ignored: dungeon={DungeonName} map={MapId} challenge={ChallengeModeId} level={KeystoneLevel} elapsedSinceOriginalStart={ElapsedSinceOriginalStart} action=KeepRecording",
            activeChallenge.DungeonName,
            activeChallenge.MapId,
            activeChallenge.ChallengeModeId,
            activeChallenge.KeystoneLevel,
            duplicateStart.StartedAt - activeChallenge.StartedAt
        );
    }

    private void LogChallengeSoftStopSuspected(
        ChallengeRecordingContext activeChallenge,
        ChallengeSoftStopEvidence evidence
    )
    {
        if (evidence.ZoneId is { } zoneId)
        {
            logger.LogInformation(
                "M+ soft stop suspected: activeDungeon={ActiveDungeonName} activeMap={ActiveMapId} event={EventName} zone={ZoneId} zoneName={ZoneName} instanceType={InstanceType} watchdogTimeout={WatchdogTimeout}",
                activeChallenge.DungeonName,
                activeChallenge.MapId,
                evidence.EventName,
                zoneId,
                evidence.ZoneName,
                evidence.InstanceType,
                _challengeWatchdogTimeout
            );
            return;
        }

        logger.LogInformation(
            "M+ soft stop suspected: activeDungeon={ActiveDungeonName} activeMap={ActiveMapId} event={EventName} uiMap={UiMapId} mapName={MapName} watchdogTimeout={WatchdogTimeout}",
            activeChallenge.DungeonName,
            activeChallenge.MapId,
            evidence.EventName,
            evidence.UiMapId,
            evidence.MapName,
            _challengeWatchdogTimeout
        );
    }

    private void LogChallengeWatchdogCancelled(
        string reason,
        ChallengeRecordingContext activeChallenge,
        ChallengeRecordingContext? challengeStart,
        ZoneChangeContext? zoneChange,
        MapChangeContext? mapChange
    )
    {
        if (challengeStart is not null)
        {
            logger.LogInformation(
                "M+ watchdog cancelled: reason={Reason} dungeon={DungeonName} map={MapId} challenge={ChallengeModeId} level={KeystoneLevel} action=KeepRecording",
                reason,
                challengeStart.DungeonName,
                challengeStart.MapId,
                challengeStart.ChallengeModeId,
                challengeStart.KeystoneLevel
            );
            return;
        }

        if (zoneChange is not null)
        {
            logger.LogInformation(
                "M+ watchdog cancelled: reason={Reason} zone={ZoneId} zoneName={ZoneName} instanceType={InstanceType} action=KeepRecording",
                reason,
                zoneChange.ZoneId,
                zoneChange.ZoneName,
                zoneChange.InstanceType
            );
            return;
        }

        if (mapChange is not null)
        {
            logger.LogInformation(
                "M+ watchdog cancelled: reason={Reason} uiMap={UiMapId} mapName={MapName} action=KeepRecording",
                reason,
                mapChange.UiMapId,
                mapChange.MapName
            );
            return;
        }

        logger.LogInformation(
            "M+ watchdog cancelled: reason={Reason} dungeon={DungeonName} map={MapId} challenge={ChallengeModeId} level={KeystoneLevel} action=KeepRecording",
            reason,
            activeChallenge.DungeonName,
            activeChallenge.MapId,
            activeChallenge.ChallengeModeId,
            activeChallenge.KeystoneLevel
        );
    }

    private void LogChallengeWatchdogExpired(
        ChallengeRecordingContext activeChallenge,
        ChallengeWatchdogState state,
        DateTimeOffset expiredAt
    )
    {
        var evidence = state.Evidence;

        if (evidence.ZoneId is { } zoneId)
        {
            logger.LogInformation(
                "M+ watchdog expired: activeDungeon={ActiveDungeonName} activeMap={ActiveMapId} firstSoftSignal={EventName} zone={ZoneId} zoneName={ZoneName} instanceType={InstanceType} elapsed={Elapsed} action=StopRecording",
                activeChallenge.DungeonName,
                activeChallenge.MapId,
                evidence.EventName,
                zoneId,
                evidence.ZoneName,
                evidence.InstanceType,
                expiredAt - evidence.OccurredAt
            );
            return;
        }

        logger.LogInformation(
            "M+ watchdog expired: activeDungeon={ActiveDungeonName} activeMap={ActiveMapId} firstSoftSignal={EventName} uiMap={UiMapId} mapName={MapName} elapsed={Elapsed} action=StopRecording",
            activeChallenge.DungeonName,
            activeChallenge.MapId,
            evidence.EventName,
            evidence.UiMapId,
            evidence.MapName,
            expiredAt - evidence.OccurredAt
        );
    }

    private sealed record ChallengeWatchdogState(
        ChallengeRecordingContext Challenge,
        ChallengeSoftStopEvidence Evidence,
        DateTimeOffset ExpiresAt
    );

    private sealed record EncounterPullKey(int EncounterId, int DifficultyId)
    {
        public static EncounterPullKey From(EncounterRecordingContext context)
        {
            return new EncounterPullKey(context.EncounterId, context.DifficultyId);
        }
    }

    private sealed record ChallengeSoftStopEvidence(
        string EventName,
        DateTimeOffset OccurredAt,
        int? ZoneId,
        string? ZoneName,
        int? InstanceType,
        int? UiMapId,
        string? MapName
    )
    {
        public static ChallengeSoftStopEvidence FromZoneChange(ZoneChangeContext zoneChange)
        {
            return new ChallengeSoftStopEvidence(
                WowEvents.ZoneChange,
                zoneChange.ChangedAt,
                zoneChange.ZoneId,
                zoneChange.ZoneName,
                zoneChange.InstanceType,
                null,
                null
            );
        }

        public static ChallengeSoftStopEvidence FromMapChange(MapChangeContext mapChange)
        {
            return new ChallengeSoftStopEvidence(
                WowEvents.MapChange,
                mapChange.ChangedAt,
                null,
                null,
                null,
                mapChange.UiMapId,
                mapChange.MapName
            );
        }
    }
}
