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
        CombatLogReaderState.WaitingForLogsDirectory,
        null,
        null,
        null);

    private readonly SettingsBootstrapper _settingsBootstrapper;
    private readonly Func<SettingsProvider, IRecordingService> _createRecordingService;
    private readonly Func<string, ICombatLogMonitor> _createCombatLogMonitor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApplicationController> _logger;
    private readonly SemaphoreSlim _lifetimeLock = new(1, 1);
    private readonly object _notificationLock = new();
    private Task _notificationQueue = Task.CompletedTask;
    private ApplicationStatus _status = new(null, InitialRecordingStatus, InitialCombatLogStatus);
    private RecordingCoordinator? _recordingCoordinator;
    private ICombatLogMonitor? _combatLogMonitor;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
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
            loggerFactory)
    {
    }

    internal ApplicationController(
        SettingsBootstrapper settingsBootstrapper,
        Func<SettingsProvider, IRecordingService> createRecordingService,
        Func<string, ICombatLogMonitor> createCombatLogMonitor,
        ILoggerFactory loggerFactory)
    {
        _settingsBootstrapper = settingsBootstrapper;
        _createRecordingService = createRecordingService;
        _createCombatLogMonitor = createCombatLogMonitor;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ApplicationController>();
    }

    public event Action<ApplicationStatus>? StatusChanged;

    public ApplicationStatus Status => Volatile.Read(ref _status);

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

            var settings = await _settingsBootstrapper.LoadEffectiveAsync(cancellationToken)
                ?? throw new InvalidOperationException("Could not load valid effective settings.");
            var settingsProvider = new SettingsProvider(settings);
            var recordingCoordinator = new RecordingCoordinator(
                _createRecordingService(settingsProvider),
                _loggerFactory.CreateLogger<RecordingCoordinator>());
            recordingCoordinator.StatusChanged += OnRecordingStatusChanged;

            _recordingCoordinator = recordingCoordinator;
            OperatingSystemActions = new OperatingSystemActions(settingsProvider);
            UpdateStatus(_ => new ApplicationStatus(
                settings,
                recordingCoordinator.Status,
                InitialCombatLogStatus));

            if (settings.WowLogsDirectory is not null)
            {
                StartCombatLogMonitoring(settings.WowLogsDirectory, settingsProvider);
            }
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
            await StopCombatLogMonitoringAsync();

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

    private RecordingCoordinator GetRecordingCoordinator()
    {
        return _recordingCoordinator
            ?? throw new InvalidOperationException("The application controller has not been started.");
    }

    private void OnRecordingStatusChanged(RecordingCoordinatorStatus status)
    {
        UpdateStatus(current => current with { Recording = status });
    }

    private void OnCombatLogStatusChanged(CombatLogReaderStatus status)
    {
        UpdateStatus(current => current with { CombatLog = status });
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
