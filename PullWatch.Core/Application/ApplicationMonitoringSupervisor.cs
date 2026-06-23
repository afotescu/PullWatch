using Microsoft.Extensions.Logging;

namespace PullWatch;

internal sealed class ApplicationMonitoringSupervisor(
    Func<string, DateTimeOffset?, Func<bool>, ICombatLogMonitor> createCombatLogMonitor,
    Func<IWowProcessMonitor> createWowProcessMonitor,
    RecordingCoordinator recordingCoordinator,
    SettingsProvider settingsProvider,
    ApplicationStatusPublisher statusPublisher,
    ILoggerFactory loggerFactory
)
{
    private readonly ILogger<ApplicationMonitoringSupervisor> _logger =
        loggerFactory.CreateLogger<ApplicationMonitoringSupervisor>();
    private readonly SemaphoreSlim _monitorLock = new(1, 1);
    private ICombatLogMonitor? _combatLogMonitor;
    private IWowProcessMonitor? _wowProcessMonitor;
    private CancellationTokenSource? _combatLogCancellation;
    private CancellationTokenSource? _wowProcessCancellation;
    private Task? _combatLogTask;
    private Task? _wowProcessTask;
    private WowProcessSession? _combatLogSession;
    private bool _isActive = true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _monitorLock.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartWowProcessMonitoring();
            await ApplyCombatLogMonitoringForCurrentStateAsync();
        }
        finally
        {
            _monitorLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _monitorLock.WaitAsync(CancellationToken.None);

        try
        {
            _isActive = false;
            await StopCombatLogMonitoringAsync();
            await StopWowProcessMonitoringAsync();
        }
        finally
        {
            _monitorLock.Release();
        }
    }

    public async Task ApplySettingsChangeAsync()
    {
        await _monitorLock.WaitAsync(CancellationToken.None);

        try
        {
            if (!_isActive)
            {
                return;
            }

            await StopCombatLogMonitoringAsync();
            statusPublisher.Update(status =>
                status with
                {
                    CombatLog = GetInactiveCombatLogStatus(
                        status.EffectiveSettings,
                        status.WowProcess
                    ),
                }
            );
            await ApplyCombatLogMonitoringForCurrentStateAsync();
        }
        finally
        {
            _monitorLock.Release();
        }
    }

    public static CombatLogReaderStatus GetInactiveCombatLogStatus(
        PullWatchSettings? settings,
        WowProcessStatus wowProcess
    )
    {
        return new CombatLogReaderStatus(
            !wowProcess.IsWindowAvailable ? CombatLogReaderState.WaitingForWow
                : settings?.WowLogsDirectory is null ? CombatLogReaderState.WaitingForLogsDirectory
                : CombatLogReaderState.WaitingForCombatLog,
            null,
            null,
            null
        );
    }

    private void StartCombatLogMonitoring(string logsDirectory, WowProcessSession wowProcessSession)
    {
        if (wowProcessSession.ProcessStartedAtUtc is null)
        {
            _logger.LogInformation(
                "Starting combat-log monitoring for WoW process {WowProcessId}; process start time unavailable, using latest-file discovery fallback",
                wowProcessSession.ProcessId
            );
        }
        else
        {
            _logger.LogInformation(
                "Starting combat-log monitoring for WoW process {WowProcessId} started at {WowProcessStartedAtUtc}; filtering combat logs by creation time",
                wowProcessSession.ProcessId,
                wowProcessSession.ProcessStartedAtUtc
            );
        }

        var monitor = createCombatLogMonitor(
            logsDirectory,
            wowProcessSession.ProcessStartedAtUtc,
            CanDiscoverCombatLog
        );
        var eventHandler = new CombatLogEventHandler(
            recordingCoordinator,
            settingsProvider,
            loggerFactory.CreateLogger<CombatLogEventHandler>()
        );
        var cancellation = new CancellationTokenSource();

        monitor.StatusChanged += OnCombatLogStatusChanged;
        _combatLogMonitor = monitor;
        _combatLogSession = wowProcessSession;
        _combatLogCancellation = cancellation;
        statusPublisher.Update(status => status with { CombatLog = monitor.Status });
        _combatLogTask = Task.Run(
            () => MonitorCombatLogsAsync(monitor, eventHandler, cancellation.Token),
            CancellationToken.None
        );
    }

    private void StartWowProcessMonitoring()
    {
        var monitor = createWowProcessMonitor();
        var cancellation = new CancellationTokenSource();

        monitor.StatusChanged += OnWowProcessStatusChanged;
        _wowProcessMonitor = monitor;
        _wowProcessCancellation = cancellation;
        statusPublisher.Update(status => status with { WowProcess = monitor.Status });
        _wowProcessTask = Task.Run(
            () => MonitorWowProcessAsync(monitor, cancellation.Token),
            CancellationToken.None
        );
    }

    private async Task MonitorWowProcessAsync(
        IWowProcessMonitor monitor,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await monitor.WatchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WoW process monitoring stopped unexpectedly");
        }
    }

    private async Task MonitorCombatLogsAsync(
        ICombatLogMonitor monitor,
        CombatLogEventHandler eventHandler,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await monitor.ReadAsync(eventHandler.HandleAsync, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Combat-log monitoring stopped unexpectedly");

            if (!ReferenceEquals(_combatLogMonitor, monitor))
            {
                return;
            }

            statusPublisher.Update(status =>
                status with
                {
                    CombatLog = GetInactiveCombatLogStatus(
                        status.EffectiveSettings,
                        status.WowProcess
                    ) with
                    {
                        LastFileSystemError = exception,
                    },
                }
            );
        }
        finally
        {
            await eventHandler.DisposeAsync();
        }
    }

    private async Task StopCombatLogMonitoringAsync()
    {
        var cancellation = _combatLogCancellation;
        var task = _combatLogTask;
        var monitor = _combatLogMonitor;
        _combatLogCancellation = null;
        _combatLogTask = null;
        _combatLogMonitor = null;
        _combatLogSession = null;

        if (monitor is not null)
        {
            monitor.StatusChanged -= OnCombatLogStatusChanged;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();

        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task StopWowProcessMonitoringAsync()
    {
        var cancellation = _wowProcessCancellation;
        var task = _wowProcessTask;
        var monitor = _wowProcessMonitor;
        _wowProcessCancellation = null;
        _wowProcessTask = null;
        _wowProcessMonitor = null;

        if (monitor is not null)
        {
            monitor.StatusChanged -= OnWowProcessStatusChanged;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();

        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        finally
        {
            cancellation.Dispose();

            if (monitor is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void OnCombatLogStatusChanged(CombatLogReaderStatus status)
    {
        statusPublisher.Update(current => current with { CombatLog = status });
    }

    private void OnWowProcessStatusChanged(WowProcessStatus status)
    {
        statusPublisher.Update(current => current with { WowProcess = status });
        _ = ApplyCombatLogMonitoringForWowChangeAsync();
    }

    private async Task ApplyCombatLogMonitoringForWowChangeAsync()
    {
        await _monitorLock.WaitAsync(CancellationToken.None);

        try
        {
            if (!_isActive)
            {
                return;
            }

            await ApplyCombatLogMonitoringForCurrentStateAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not apply WoW-gated combat-log monitoring state");
            statusPublisher.Update(status =>
                status with
                {
                    CombatLog = GetInactiveCombatLogStatus(
                        status.EffectiveSettings,
                        status.WowProcess
                    ) with
                    {
                        LastFileSystemError = exception,
                    },
                }
            );
        }
        finally
        {
            _monitorLock.Release();
        }
    }

    private async Task ApplyCombatLogMonitoringForCurrentStateAsync()
    {
        var status = statusPublisher.Status;
        var settings = status.EffectiveSettings;
        var wowProcessSession = GetWowProcessSession(status.WowProcess);

        if (settings is null || wowProcessSession is null || settings.WowLogsDirectory is null)
        {
            await StopCombatLogMonitoringAsync();
            statusPublisher.Update(current =>
                current with
                {
                    CombatLog = GetInactiveCombatLogStatus(settings, current.WowProcess),
                }
            );
            return;
        }

        if (_combatLogMonitor is not null)
        {
            if (_combatLogSession == wowProcessSession)
            {
                return;
            }

            _logger.LogInformation(
                "Restarting combat-log monitoring because the WoW session changed from process {PreviousWowProcessId} started at {PreviousWowProcessStartedAtUtc} to process {WowProcessId} started at {WowProcessStartedAtUtc}",
                _combatLogSession?.ProcessId,
                _combatLogSession?.ProcessStartedAtUtc,
                wowProcessSession.ProcessId,
                wowProcessSession.ProcessStartedAtUtc
            );
            await StopCombatLogMonitoringAsync();
        }

        StartCombatLogMonitoring(settings.WowLogsDirectory, wowProcessSession);
    }

    private bool CanDiscoverCombatLog()
    {
        return recordingCoordinator.Status.State == RecordingCoordinatorState.Idle;
    }

    private static WowProcessSession? GetWowProcessSession(WowProcessStatus status)
    {
        return status is { IsWindowAvailable: true, ProcessId: { } processId }
            ? new WowProcessSession(processId, status.ProcessStartedAtUtc)
            : null;
    }

    private sealed record WowProcessSession(int ProcessId, DateTimeOffset? ProcessStartedAtUtc);
}
