using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PullWatch;

internal sealed class ChallengeModeLifecycle(
    RecordingCoordinator recordingCoordinator,
    ILogger logger,
    TimeSpan? watchdogTimeout = null
) : IAsyncDisposable
{
    private static readonly TimeSpan DefaultWatchdogTimeout = TimeSpan.FromSeconds(10);
    private static readonly StringComparer DungeonNameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly TimeSpan _watchdogTimeout = watchdogTimeout ?? DefaultWatchdogTimeout;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private ChallengeWatchdogState? _watchdog;
    private CancellationTokenSource? _watchdogCancellation;
    private Task? _watchdogTask;
    private bool _disposed;

    public async Task HandleEventAsync(
        CombatLogEvent combatLogEvent,
        Func<long, DateTimeOffset, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken
    )
    {
        await _lifecycleLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var eventTimestamp = Stopwatch.GetTimestamp();
            var receivedAt = DateTimeOffset.Now;
            var occurredAt = combatLogEvent.LoggedAt ?? receivedAt;
            var eventName = combatLogEvent.Name;

            if (eventName is not WowEvents.CombatLogVersion and not WowEvents.ChallengeModeEnd)
            {
                await ExpireWatchdogIfDueAsync(occurredAt, cancellationToken);
                ClearWatchdogIfRecordingEnded();
            }

            await handleEventAsync(eventTimestamp, occurredAt, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? watchdogTask;
        CancellationTokenSource? watchdogCancellation;

        await _lifecycleLock.WaitAsync(CancellationToken.None);

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watchdogTask = _watchdogTask;
            watchdogCancellation = _watchdogCancellation;
            _watchdog = null;
            _watchdogTask = null;
            _watchdogCancellation = null;
            watchdogCancellation?.Cancel();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await ObserveCanceledTaskAsync(watchdogTask, watchdogCancellation);
    }

    public async Task HandleStartAsync(
        string eventName,
        ChallengeRecordingContext context,
        CancellationToken cancellationToken
    )
    {
        LogMythicPlusStart(context);

        if (GetActiveChallengeContext() is { } activeChallenge)
        {
            if (IsSameChallenge(activeChallenge, context))
            {
                LogSameChallengeStartIgnored(activeChallenge, context);
                CancelWatchdogForRecovery(
                    "SameChallengeStart",
                    activeChallenge,
                    challengeStart: context
                );
                LogCommandResult(eventName, RecordingCommandResult.AlreadyActive);
                return;
            }

            LogDifferentChallengeHardStop(activeChallenge, context);
            ClearWatchdog();
            var stopResult = await recordingCoordinator.StopAutomaticAsync(
                RecordingOwner.ChallengeMode,
                null,
                null,
                cancellationToken
            );
            LogCommandResult($"{eventName}:StopDifferentChallenge", stopResult);

            if (stopResult is not RecordingCommandResult.Stopped)
            {
                return;
            }
        }

        var result = await recordingCoordinator.StartAutomaticAsync(context, cancellationToken);
        LogCommandResult(eventName, result);
    }

    public async Task HandleEndAsync(
        string eventName,
        ChallengeRecordingEnd? challengeEnd,
        CancellationToken cancellationToken
    )
    {
        LogChallengeModeEndHardStop(challengeEnd);
        ClearWatchdog();
        var result = await recordingCoordinator.StopAutomaticAsync(
            RecordingOwner.ChallengeMode,
            null,
            challengeEnd,
            cancellationToken
        );
        LogCommandResult(eventName, result);
    }

    public void HandleZoneChange(ZoneChangeContext zoneChange)
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
            CancelWatchdogForRecovery(
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
            StartOrRefreshWatchdog(
                activeChallenge,
                ChallengeSoftStopEvidence.FromZoneChange(zoneChange)
            );
        }
    }

    public void HandleMapChange(MapChangeContext mapChange)
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
            CancelWatchdogForRecovery(
                "MapReturnedToDungeon",
                activeChallenge,
                mapChange: mapChange
            );
            return;
        }

        StartOrRefreshWatchdog(activeChallenge, ChallengeSoftStopEvidence.FromMapChange(mapChange));
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

    private bool IsNonMythicPlusDungeonZoneSuspected(ChallengeRecordingContext activeChallenge)
    {
        var evidence = _watchdog?.Evidence;

        return evidence?.EventName == WowEvents.ZoneChange
            && evidence.ZoneId == activeChallenge.MapId
            && evidence.InstanceType != WowDifficultyIds.MythicPlus;
    }

    private async Task ExpireWatchdogIfDueAsync(
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken
    )
    {
        var state = _watchdog;

        if (state is null || occurredAt < state.ExpiresAt)
        {
            return;
        }

        await ExpireWatchdogAsync(state.ExpiresAt, cancellationToken);
    }

    private async Task ExpireWatchdogFromTimerAsync(
        ChallengeWatchdogState state,
        CancellationToken cancellationToken
    )
    {
        await _lifecycleLock.WaitAsync(cancellationToken);

        try
        {
            if (!ReferenceEquals(_watchdog, state))
            {
                return;
            }

            await ExpireWatchdogAsync(state.ExpiresAt, CancellationToken.None);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ExpireWatchdogAsync(
        DateTimeOffset expiredAt,
        CancellationToken cancellationToken
    )
    {
        var state = ClearWatchdog();
        var activeChallenge = GetActiveChallengeContext();

        if (
            state is null
            || activeChallenge is null
            || !IsSameChallenge(activeChallenge, state.Challenge)
        )
        {
            return;
        }

        LogWatchdogExpired(activeChallenge, state, expiredAt);
        var result = await recordingCoordinator.StopAutomaticAsync(
            RecordingOwner.ChallengeMode,
            null,
            CreateInferredDepletedChallengeEnd(activeChallenge, expiredAt),
            cancellationToken
        );
        LogCommandResult("MythicPlusWatchdog", result);
    }

    private void StartOrRefreshWatchdog(
        ChallengeRecordingContext activeChallenge,
        ChallengeSoftStopEvidence evidence
    )
    {
        ClearWatchdog();

        var cancellation = new CancellationTokenSource();
        var state = new ChallengeWatchdogState(
            activeChallenge,
            evidence,
            evidence.OccurredAt + _watchdogTimeout
        );

        _watchdog = state;
        _watchdogCancellation = cancellation;
        _watchdogTask = RunWatchdogAsync(state, cancellation.Token);
        LogSoftStopSuspected(activeChallenge, evidence);
    }

    private async Task RunWatchdogAsync(
        ChallengeWatchdogState state,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(_watchdogTimeout, cancellationToken);
            await ExpireWatchdogFromTimerAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "M+ watchdog failed");
        }
    }

    private void CancelWatchdogForRecovery(
        string reason,
        ChallengeRecordingContext activeChallenge,
        ChallengeRecordingContext? challengeStart = null,
        ZoneChangeContext? zoneChange = null,
        MapChangeContext? mapChange = null
    )
    {
        if (ClearWatchdog() is null)
        {
            return;
        }

        LogWatchdogCancelled(reason, activeChallenge, challengeStart, zoneChange, mapChange);
    }

    private ChallengeWatchdogState? ClearWatchdog()
    {
        var state = _watchdog;
        var cancellation = _watchdogCancellation;
        var task = _watchdogTask;

        _watchdog = null;
        _watchdogCancellation = null;
        _watchdogTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
            _ = DisposeWatchdogCancellationAsync(task, cancellation);
        }

        return state;
    }

    private void ClearWatchdogIfRecordingEnded()
    {
        var state = _watchdog;

        if (state is null)
        {
            return;
        }

        var activeChallenge = GetActiveChallengeContext();

        if (activeChallenge is null || !IsSameChallenge(activeChallenge, state.Challenge))
        {
            ClearWatchdog();
        }
    }

    private static async Task DisposeWatchdogCancellationAsync(
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

    private void LogSoftStopSuspected(
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
                _watchdogTimeout
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
            _watchdogTimeout
        );
    }

    private void LogWatchdogCancelled(
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

    private void LogWatchdogExpired(
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
