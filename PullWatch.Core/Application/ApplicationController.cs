using System.Diagnostics;
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
        null
    );

    private static readonly CombatLogReaderStatus InitialCombatLogStatus = new(
        CombatLogReaderState.WaitingForWow,
        null,
        null,
        null
    );

    private static readonly WowProcessStatus InitialWowProcessStatus = new(
        WowProcessState.WaitingForProcess,
        null,
        null,
        null
    );

    private static readonly ApplicationStatus InitialStatus = new(
        null,
        InitialRecordingStatus,
        InitialCombatLogStatus,
        InitialWowProcessStatus
    );

    private readonly SettingsBootstrapper _settingsBootstrapper;
    private readonly Func<SettingsProvider, IRecordingService> _createRecordingService;
    private readonly Func<string, ICombatLogMonitor> _createCombatLogMonitor;
    private readonly Func<IWowProcessMonitor> _createWowProcessMonitor;
    private readonly IRecordingStorageInitializer _storageInitializer;
    private readonly RecordingCatalog? _recordingCatalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApplicationController> _logger;
    private readonly ApplicationStatusPublisher _statusPublisher;
    private readonly SemaphoreSlim _lifetimeLock = new(1, 1);
    private RecordingCoordinator? _recordingCoordinator;
    private ApplicationSettingsService? _settingsService;
    private ApplicationMonitoringSupervisor? _monitoringSupervisor;
    private bool _disposed;
    private int _disposeStarted;

    public ApplicationController(ILoggerFactory loggerFactory)
        : this(
            new SettingsBootstrapper(
                new SettingsStore(),
                loggerFactory.CreateLogger<SettingsBootstrapper>()
            ),
            settings => new ScreenRecordingService(
                settings,
                loggerFactory.CreateLogger<ScreenRecordingService>()
            ),
            path => new CombatLogReader(path, loggerFactory.CreateLogger<CombatLogReader>()),
            loggerFactory,
            () => new WowProcessMonitor(loggerFactory.CreateLogger<WowProcessMonitor>()),
            CreateDefaultStorageInitializer(loggerFactory),
            CreateDefaultRecordingCatalog()
        ) { }

    internal ApplicationController(
        SettingsBootstrapper settingsBootstrapper,
        Func<SettingsProvider, IRecordingService> createRecordingService,
        Func<string, ICombatLogMonitor> createCombatLogMonitor,
        ILoggerFactory loggerFactory,
        Func<IWowProcessMonitor> createWowProcessMonitor,
        IRecordingStorageInitializer? storageInitializer = null,
        RecordingCatalog? recordingCatalog = null
    )
    {
        _settingsBootstrapper = settingsBootstrapper;
        _createRecordingService = createRecordingService;
        _createCombatLogMonitor = createCombatLogMonitor;
        _createWowProcessMonitor =
            createWowProcessMonitor
            ?? throw new ArgumentNullException(nameof(createWowProcessMonitor));
        _storageInitializer = storageInitializer ?? NoOpRecordingStorageInitializer.Instance;
        _recordingCatalog = recordingCatalog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ApplicationController>();
        _statusPublisher = new ApplicationStatusPublisher(
            InitialStatus,
            loggerFactory.CreateLogger<ApplicationStatusPublisher>()
        );
    }

    public event Action<ApplicationStatus>? StatusChanged
    {
        add => _statusPublisher.StatusChanged += value;
        remove => _statusPublisher.StatusChanged -= value;
    }

    public ApplicationStatus Status => _statusPublisher.Status;

    public bool StartedWithCreatedSettingsFile { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (_recordingCoordinator is not null)
            {
                return;
            }

            var bootstrapResult =
                await _settingsBootstrapper.LoadEffectiveWithMetadataAsync(cancellationToken)
                ?? throw new InvalidOperationException("Could not load valid effective settings.");
            var settings = bootstrapResult.Settings;
            await _storageInitializer.InitializeAsync(cancellationToken);
            var settingsProvider = new SettingsProvider(settings);
            var settingsService = new ApplicationSettingsService(
                _settingsBootstrapper.Store,
                settingsProvider
            );
            var recordingCoordinator = new RecordingCoordinator(
                _createRecordingService(settingsProvider),
                _loggerFactory.CreateLogger<RecordingCoordinator>(),
                recordingCatalog: _recordingCatalog
            );
            var monitoringSupervisor = new ApplicationMonitoringSupervisor(
                _createCombatLogMonitor,
                _createWowProcessMonitor,
                recordingCoordinator,
                settingsProvider,
                _statusPublisher,
                _loggerFactory
            );
            recordingCoordinator.StatusChanged += OnRecordingStatusChanged;

            try
            {
                _recordingCoordinator = recordingCoordinator;
                _settingsService = settingsService;
                _monitoringSupervisor = monitoringSupervisor;
                StartedWithCreatedSettingsFile = bootstrapResult.CreatedSettingsFile;
                UpdateStatus(status => new ApplicationStatus(
                    settings,
                    recordingCoordinator.Status,
                    ApplicationMonitoringSupervisor.GetInactiveCombatLogStatus(
                        settings,
                        status.WowProcess
                    ),
                    status.WowProcess
                ));
                await monitoringSupervisor.StartAsync(cancellationToken);
            }
            catch
            {
                await RollBackFailedStartAsync(recordingCoordinator, monitoringSupervisor);
                throw;
            }
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public async Task<RecordingCommandResult> StartManualRecordingAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunRecordingCommandAsync(
            coordinator => coordinator.StartManualAsync(cancellationToken),
            cancellationToken
        );
    }

    public async Task<RecordingCommandResult> StopManualRecordingAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunRecordingCommandAsync(
            coordinator => coordinator.StopManualAsync(cancellationToken),
            cancellationToken
        );
    }

    public async Task<RecordingCommandResult> FinalizeRecordingForExitAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunRecordingCommandAsync(
            coordinator => coordinator.ShutdownAsync(cancellationToken),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<RecordingCatalogFile>> ListRecordingsAsync(
        string recordingsDirectory,
        CancellationToken cancellationToken
    )
    {
        return _recordingCatalog is null
            ? Task.FromResult<IReadOnlyList<RecordingCatalogFile>>([])
            : _recordingCatalog.ListAvailableFilesAsync(recordingsDirectory, cancellationToken);
    }

    public Task OpenRecordingsFolderAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var settingsService =
            _settingsService
            ?? throw new InvalidOperationException(
                "The application controller has not been started."
            );
        var path =
            settingsService.Current.RecordingsDirectory
            ?? throw new InvalidOperationException("Recordings directory was not configured.");

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public async Task<SettingsSaveResult> SaveSettingsAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var coordinator = GetRecordingCoordinator();
            var settingsService =
                _settingsService
                ?? throw new InvalidOperationException(
                    "The application controller has not been started."
                );

            if (coordinator.Status.State != RecordingCoordinatorState.Idle)
            {
                return new SettingsSaveResult(SettingsSaveStatus.RecordingActive, null, []);
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
                await GetMonitoringSupervisor().ApplySettingsChangeAsync();
                return result;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not apply the new combat-log directory");
                UpdateStatus(status =>
                    status with
                    {
                        CombatLog = ApplicationMonitoringSupervisor.GetInactiveCombatLogStatus(
                            savedSettings,
                            status.WowProcess
                        ) with
                        {
                            LastFileSystemError = exception,
                        },
                    }
                );
                return result with
                {
                    Status = SettingsSaveStatus.ApplicationFailed,
                    Error = exception,
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
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(uiSettings);

        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var settingsService =
                _settingsService
                ?? throw new InvalidOperationException(
                    "The application controller has not been started."
                );
            var result = await settingsService.SaveAsync(
                settingsService.Current with
                {
                    Ui = uiSettings,
                },
                cancellationToken
            );

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
        if (IsDisposed)
        {
            return;
        }

        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            if (IsDisposed)
            {
                return;
            }

            await ShutdownCoreAsync();
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        await _lifetimeLock.WaitAsync(CancellationToken.None);

        try
        {
            await ShutdownCoreAsync();
        }
        finally
        {
            _lifetimeLock.Release();
            _lifetimeLock.Dispose();
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed);

    private static RecordingStorageInitializer CreateDefaultStorageInitializer(
        ILoggerFactory loggerFactory
    )
    {
        return new RecordingStorageInitializer(
            new SqliteConnectionFactory(new RecordingDatabasePathProvider()),
            loggerFactory
        );
    }

    private static RecordingCatalog CreateDefaultRecordingCatalog()
    {
        return new RecordingCatalog(
            new RecordingCatalogRepository(
                new SqliteConnectionFactory(new RecordingDatabasePathProvider())
            )
        );
    }

    private RecordingCoordinator GetRecordingCoordinator()
    {
        return _recordingCoordinator
            ?? throw new InvalidOperationException(
                "The application controller has not been started."
            );
    }

    private ApplicationMonitoringSupervisor GetMonitoringSupervisor()
    {
        return _monitoringSupervisor
            ?? throw new InvalidOperationException(
                "The application controller has not been started."
            );
    }

    private async Task<RecordingCommandResult> RunRecordingCommandAsync(
        Func<RecordingCoordinator, Task<RecordingCommandResult>> command,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return await command(GetRecordingCoordinator());
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var coordinator = _recordingCoordinator;

        if (coordinator is null)
        {
            return;
        }

        var monitoringSupervisor = _monitoringSupervisor;
        _recordingCoordinator = null;
        _settingsService = null;
        _monitoringSupervisor = null;

        if (monitoringSupervisor is not null)
        {
            await monitoringSupervisor.StopAsync();
        }

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

    private async Task RollBackFailedStartAsync(
        RecordingCoordinator recordingCoordinator,
        ApplicationMonitoringSupervisor monitoringSupervisor
    )
    {
        _recordingCoordinator = null;
        _settingsService = null;
        _monitoringSupervisor = null;
        StartedWithCreatedSettingsFile = false;
        recordingCoordinator.StatusChanged -= OnRecordingStatusChanged;

        try
        {
            await monitoringSupervisor.StopAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not stop monitoring after startup failure");
        }

        try
        {
            await recordingCoordinator.DisposeAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not dispose recording coordinator after startup failure"
            );
        }

        _statusPublisher.Set(InitialStatus);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    private void OnRecordingStatusChanged(RecordingCoordinatorStatus status)
    {
        UpdateStatus(current => current with { Recording = status });
    }

    private void UpdateStatus(Func<ApplicationStatus, ApplicationStatus> update)
    {
        _statusPublisher.Update(update);
    }
}
