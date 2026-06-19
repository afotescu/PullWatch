using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class ApplicationController : IAsyncDisposable
{
    private static readonly RecordingCoordinatorStatus InitialRecordingStatus = new(
        RecordingCoordinatorState.Idle,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    private static readonly CombatLogReaderStatus InitialCombatLogStatus = new(
        CombatLogReaderState.WaitingForWow,
        null,
        null,
        null);

    private static readonly WowProcessStatus InitialWowProcessStatus = new(
        WowProcessState.WaitingForProcess,
        null,
        null,
        null);

    private readonly SettingsBootstrapper _settingsBootstrapper;
    private readonly Func<SettingsProvider, IRecordingService> _createRecordingService;
    private readonly Func<string, ICombatLogMonitor> _createCombatLogMonitor;
    private readonly Func<IWowProcessMonitor>? _createWowProcessMonitor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApplicationController> _logger;
    private readonly SemaphoreSlim _lifetimeLock = new(1, 1);
    private readonly object _notificationLock = new();
    private Task _notificationQueue = Task.CompletedTask;
    private ApplicationStatus _status = new(null, InitialRecordingStatus, InitialCombatLogStatus, InitialWowProcessStatus);
    private RecordingCoordinator? _recordingCoordinator;
    private ApplicationSettingsService? _settingsService;
    private SettingsProvider? _settingsProvider;
    private ICombatLogMonitor? _combatLogMonitor;
    private IWowProcessMonitor? _wowProcessMonitor;
    private CancellationTokenSource? _monitorCancellation;
    private CancellationTokenSource? _wowProcessCancellation;
    private Task? _monitorTask;
    private Task? _wowProcessTask;
    private bool _disposed;

    public ApplicationController(ILoggerFactory loggerFactory)
        : this(
            new SettingsBootstrapper(
                new SettingsStore(),
                loggerFactory.CreateLogger<SettingsBootstrapper>()),
            settings => new ScreenRecordingService(
                settings,
                loggerFactory.CreateLogger<ScreenRecordingService>()),
            path => new CombatLogReader(
                path,
                loggerFactory.CreateLogger<CombatLogReader>()),
            loggerFactory,
            () => new WowProcessMonitor(
                loggerFactory.CreateLogger<WowProcessMonitor>()))
    {
    }

    internal ApplicationController(
        SettingsBootstrapper settingsBootstrapper,
        Func<SettingsProvider, IRecordingService> createRecordingService,
        Func<string, ICombatLogMonitor> createCombatLogMonitor,
        ILoggerFactory loggerFactory,
        Func<IWowProcessMonitor>? createWowProcessMonitor = null)
    {
        _settingsBootstrapper = settingsBootstrapper;
        _createRecordingService = createRecordingService;
        _createCombatLogMonitor = createCombatLogMonitor;
        _createWowProcessMonitor = createWowProcessMonitor;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ApplicationController>();
    }

    public event Action<ApplicationStatus>? StatusChanged;

    public ApplicationStatus Status => Volatile.Read(ref _status);

    public bool StartedWithCreatedSettingsFile { get; private set; }

    public IOperatingSystemActions? OperatingSystemActions { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recordingCoordinator is not null)
            {
                return;
            }

            var bootstrapResult = await _settingsBootstrapper.LoadEffectiveWithMetadataAsync(cancellationToken)
                ?? throw new InvalidOperationException("Could not load valid effective settings.");
            var settings = bootstrapResult.Settings;
            var settingsProvider = new SettingsProvider(settings);
            var settingsService = new ApplicationSettingsService(
                _settingsBootstrapper.Store,
                settingsProvider);
            var recordingCoordinator = new RecordingCoordinator(
                _createRecordingService(settingsProvider),
                _loggerFactory.CreateLogger<RecordingCoordinator>());
            recordingCoordinator.StatusChanged += OnRecordingStatusChanged;

            _recordingCoordinator = recordingCoordinator;
            _settingsProvider = settingsProvider;
            _settingsService = settingsService;
            StartedWithCreatedSettingsFile = bootstrapResult.CreatedSettingsFile;
            OperatingSystemActions = new OperatingSystemActions(settingsProvider);
            UpdateStatus(status => new ApplicationStatus(
                settings,
                recordingCoordinator.Status,
                GetInactiveCombatLogStatus(settings, status.WowProcess),
                status.WowProcess));
            StartWowProcessMonitoring();
            await ApplyCombatLogMonitoringForCurrentStateAsync();
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public Task<RecordingCommandResult> StartManualRecordingAsync(CancellationToken cancellationToken)
    {
        return GetRecordingCoordinator().StartManualAsync(cancellationToken);
    }

    public Task<RecordingCommandResult> StopManualRecordingAsync(CancellationToken cancellationToken)
    {
        return GetRecordingCoordinator().StopManualAsync(cancellationToken);
    }

    public Task<RecordingCommandResult> FinalizeRecordingForExitAsync(CancellationToken cancellationToken)
    {
        return GetRecordingCoordinator().ShutdownAsync(cancellationToken);
    }

    public async Task<SettingsSaveResult> SaveSettingsAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken)
    {
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var coordinator = GetRecordingCoordinator();
            var settingsService = _settingsService
                ?? throw new InvalidOperationException("The application controller has not been started.");

            if (coordinator.Status.State != RecordingCoordinatorState.Idle)
            {
                return new SettingsSaveResult(
                    SettingsSaveStatus.RecordingActive,
                    null,
                    []);
            }

            var previousSettings = settingsService.Current;
            var result = await settingsService.SaveAsync(settings, cancellationToken);

            if (!result.IsSaved)
            {
                return result;
            }

            var savedSettings = result.Settings!;
            UpdateStatus(status => status with { EffectiveSettings = savedSettings });

            if (PathsEqual(previousSettings.WowLogsDirectory, savedSettings.WowLogsDirectory))
            {
                return result;
            }

            try
            {
                await StopCombatLogMonitoringAsync();
                UpdateStatus(status => status with
                {
                    CombatLog = GetInactiveCombatLogStatus(savedSettings, status.WowProcess)
                });
                await ApplyCombatLogMonitoringForCurrentStateAsync();

                return result;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not apply the new combat-log directory");
                UpdateStatus(status => status with
                {
                    CombatLog = InitialCombatLogStatus with { LastFileSystemError = exception }
                });
                return result with
                {
                    Status = SettingsSaveStatus.ApplicationFailed,
                    Error = exception
                };
            }
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public async Task<SettingsSaveResult> SaveUiSettingsAsync(
        UiSettings uiSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uiSettings);

        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var settingsService = _settingsService
                ?? throw new InvalidOperationException("The application controller has not been started.");
            var result = await settingsService.SaveAsync(
                settingsService.Current with { Ui = uiSettings },
                cancellationToken);

            if (result.IsSaved)
            {
                UpdateStatus(status => status with { EffectiveSettings = result.Settings });
            }

            return result;
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            var coordinator = _recordingCoordinator;

            if (coordinator is null)
            {
                return;
            }

            _recordingCoordinator = null;
            _settingsService = null;
            _settingsProvider = null;
            await StopCombatLogMonitoringAsync();
            await StopWowProcessMonitoringAsync();

            try
            {
                await coordinator.DisposeAsync();
            }
            finally
            {
                UpdateStatus(status => status with { Recording = coordinator.Status });
                coordinator.StatusChanged -= OnRecordingStatusChanged;
            }
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await ShutdownAsync(CancellationToken.None);
        _disposed = true;
        _lifetimeLock.Dispose();
    }

    private void StartCombatLogMonitoring(string logsDirectory, SettingsProvider settingsProvider)
    {
        var monitor = _createCombatLogMonitor(logsDirectory);
        var eventHandler = new CombatLogEventHandler(
            GetRecordingCoordinator(),
            settingsProvider,
            _loggerFactory.CreateLogger<CombatLogEventHandler>());
        var cancellation = new CancellationTokenSource();

        monitor.StatusChanged += OnCombatLogStatusChanged;
        _combatLogMonitor = monitor;
        _monitorCancellation = cancellation;
        UpdateStatus(status => status with { CombatLog = monitor.Status });
        _monitorTask = Task.Run(
            () => MonitorCombatLogsAsync(monitor, eventHandler, cancellation.Token),
            CancellationToken.None);
    }

    private void StartWowProcessMonitoring()
    {
        if (_createWowProcessMonitor is null)
        {
            return;
        }

        var monitor = _createWowProcessMonitor();
        var cancellation = new CancellationTokenSource();

        monitor.StatusChanged += OnWowProcessStatusChanged;
        _wowProcessMonitor = monitor;
        _wowProcessCancellation = cancellation;
        UpdateStatus(status => status with { WowProcess = monitor.Status });
        _wowProcessTask = Task.Run(
            () => MonitorWowProcessAsync(monitor, cancellation.Token),
            CancellationToken.None);
    }

    private async Task MonitorWowProcessAsync(
        IWowProcessMonitor monitor,
        CancellationToken cancellationToken)
    {
        try
        {
            await monitor.WatchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WoW process monitoring stopped unexpectedly");
        }
    }

    private async Task MonitorCombatLogsAsync(
        ICombatLogMonitor monitor,
        CombatLogEventHandler eventHandler,
        CancellationToken cancellationToken)
    {
        try
        {
            await monitor.ReadAsync(eventHandler.HandleAsync, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Combat-log monitoring stopped unexpectedly");
        }
    }

    private async Task StopCombatLogMonitoringAsync()
    {
        var cancellation = _monitorCancellation;
        var task = _monitorTask;
        var monitor = _combatLogMonitor;
        _monitorCancellation = null;
        _monitorTask = null;
        _combatLogMonitor = null;

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

    private RecordingCoordinator GetRecordingCoordinator()
    {
        return _recordingCoordinator
            ?? throw new InvalidOperationException("The application controller has not been started.");
    }

    private SettingsProvider GetSettingsProvider()
    {
        return _settingsProvider
            ?? throw new InvalidOperationException("The application controller has not been started.");
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    private void OnRecordingStatusChanged(RecordingCoordinatorStatus status)
    {
        UpdateStatus(current => current with { Recording = status });
    }

    private void OnCombatLogStatusChanged(CombatLogReaderStatus status)
    {
        UpdateStatus(current => current with { CombatLog = status });
    }

    private void OnWowProcessStatusChanged(WowProcessStatus status)
    {
        UpdateStatus(current => current with { WowProcess = status });
        _ = ApplyCombatLogMonitoringForWowChangeAsync();
    }

    private async Task ApplyCombatLogMonitoringForWowChangeAsync()
    {
        await _lifetimeLock.WaitAsync(CancellationToken.None);

        try
        {
            if (_disposed || _recordingCoordinator is null)
            {
                return;
            }

            await ApplyCombatLogMonitoringForCurrentStateAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not apply WoW-gated combat-log monitoring state");
            UpdateStatus(status => status with
            {
                CombatLog = GetInactiveCombatLogStatus(
                    status.EffectiveSettings,
                    status.WowProcess) with
                {
                    LastFileSystemError = exception
                }
            });
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    private async Task ApplyCombatLogMonitoringForCurrentStateAsync()
    {
        var status = Status;
        var settings = status.EffectiveSettings;

        if (settings is null || !status.WowProcess.IsWindowAvailable || settings.WowLogsDirectory is null)
        {
            await StopCombatLogMonitoringAsync();
            UpdateStatus(current => current with
            {
                CombatLog = GetInactiveCombatLogStatus(settings, current.WowProcess)
            });
            return;
        }

        if (_combatLogMonitor is not null)
        {
            return;
        }

        StartCombatLogMonitoring(settings.WowLogsDirectory, GetSettingsProvider());
    }

    private static CombatLogReaderStatus GetInactiveCombatLogStatus(
        PullWatchSettings? settings,
        WowProcessStatus wowProcess)
    {
        return InitialCombatLogStatus with
        {
            State = !wowProcess.IsWindowAvailable
                ? CombatLogReaderState.WaitingForWow
                : settings?.WowLogsDirectory is null
                    ? CombatLogReaderState.WaitingForLogsDirectory
                    : CombatLogReaderState.WaitingForCombatLog
        };
    }

    private void UpdateStatus(Func<ApplicationStatus, ApplicationStatus> update)
    {
        lock (_notificationLock)
        {
            var snapshot = update(Status);
            Volatile.Write(ref _status, snapshot);
            _notificationQueue = _notificationQueue.ContinueWith(
                _ => NotifyStatusChanged(snapshot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void NotifyStatusChanged(ApplicationStatus snapshot)
    {
        var handlers = StatusChanged;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<ApplicationStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Application status subscriber failed");
            }
        }
    }
}
