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
    private readonly Func<
        string,
        DateTimeOffset?,
        Func<bool>,
        ICombatLogMonitor
    > _createCombatLogMonitor;
    private readonly Func<IWowProcessMonitor> _createWowProcessMonitor;
    private readonly Func<
        CancellationToken,
        Task<EncoderCalibrationEnvironment>
    > _resolveEncoderCalibrationEnvironment;
    private readonly IRecordingStorageInitializer _storageInitializer;
    private readonly RecordingCatalog? _recordingCatalog;
    private readonly RecordingStorageCoordinator _recordingStorageCoordinator;
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
            settings => new FfmpegRecordingService(
                settings,
                loggerFactory.CreateLogger<FfmpegRecordingService>()
            ),
            (path, wowProcessStartedAtUtc, canDiscoverCombatLog) =>
                new CombatLogReader(
                    path,
                    loggerFactory.CreateLogger<CombatLogReader>(),
                    wowProcessStartedAtUtc: wowProcessStartedAtUtc,
                    canDiscoverCombatLog: canDiscoverCombatLog
                ),
            loggerFactory,
            () => new WowProcessMonitor(loggerFactory.CreateLogger<WowProcessMonitor>()),
            CreateDefaultStorageInitializer(loggerFactory),
            CreateDefaultRecordingCatalog(),
            resolveEncoderCalibrationEnvironment: FfmpegToolPaths.ResolveEnvironmentAsync
        ) { }

    internal ApplicationController(
        SettingsBootstrapper settingsBootstrapper,
        Func<SettingsProvider, IRecordingService> createRecordingService,
        Func<string, DateTimeOffset?, Func<bool>, ICombatLogMonitor> createCombatLogMonitor,
        ILoggerFactory loggerFactory,
        Func<IWowProcessMonitor> createWowProcessMonitor,
        IRecordingStorageInitializer? storageInitializer = null,
        RecordingCatalog? recordingCatalog = null,
        RecordingStorageRetentionService? recordingStorageRetention = null,
        Func<
            CancellationToken,
            Task<EncoderCalibrationEnvironment>
        >? resolveEncoderCalibrationEnvironment = null
    )
    {
        _settingsBootstrapper = settingsBootstrapper;
        _createRecordingService = createRecordingService;
        _createCombatLogMonitor = createCombatLogMonitor;
        _createWowProcessMonitor =
            createWowProcessMonitor
            ?? throw new ArgumentNullException(nameof(createWowProcessMonitor));
        _resolveEncoderCalibrationEnvironment =
            resolveEncoderCalibrationEnvironment ?? FfmpegToolPaths.ResolveEnvironmentAsync;
        _storageInitializer = storageInitializer ?? NoOpRecordingStorageInitializer.Instance;
        _recordingCatalog = recordingCatalog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ApplicationController>();
        var storageRetention =
            recordingStorageRetention
            ?? (
                recordingCatalog is null
                    ? null
                    : new RecordingStorageRetentionService(
                        recordingCatalog,
                        loggerFactory.CreateLogger<RecordingStorageRetentionService>()
                    )
            );
        _recordingStorageCoordinator = new RecordingStorageCoordinator(
            storageRetention,
            loggerFactory.CreateLogger<RecordingStorageCoordinator>()
        );
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

    public event Action<RecordingStorageStatus>? RecordingStorageStatusChanged
    {
        add => _recordingStorageCoordinator.StatusChanged += value;
        remove => _recordingStorageCoordinator.StatusChanged -= value;
    }

    public RecordingStorageStatus RecordingStorageStatus => _recordingStorageCoordinator.Status;

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
            var encoderCalibrationEnvironment = await _resolveEncoderCalibrationEnvironment(
                cancellationToken
            );
            var videoEncoding = EncoderCalibrationStatusEvaluator.Evaluate(
                settings,
                encoderCalibrationEnvironment
            );
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
                    status.WowProcess,
                    videoEncoding
                ));
                _recordingStorageCoordinator.Start(settings);
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

    public Task<RecordingCommandResult> StartManualRecordingAsync()
    {
        return StartManualRecordingAsync(CancellationToken.None);
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

    public Task<RecordingCommandResult> StopManualRecordingAsync()
    {
        return StopManualRecordingAsync(CancellationToken.None);
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

    public Task<IReadOnlyList<RecordingCatalogFile>> ListRecordingsAsync(string recordingsDirectory)
    {
        return ListRecordingsAsync(recordingsDirectory, CancellationToken.None);
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

    public Task DeleteRecordingAsync(Guid recordingId)
    {
        return DeleteRecordingAsync(recordingId, CancellationToken.None);
    }

    public async Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _lifetimeLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (_recordingCatalog is null)
            {
                return;
            }

            var settingsService =
                _settingsService
                ?? throw new InvalidOperationException(
                    "The application controller has not been started."
                );
            var recordingsDirectory =
                settingsService.Current.RecordingsDirectory
                ?? throw new InvalidOperationException("Recordings directory was not configured.");

            await _recordingCatalog.DeleteAvailableRecordingAsync(
                recordingId,
                recordingsDirectory,
                cancellationToken
            );
        }
        finally
        {
            _lifetimeLock.Release();
        }
    }

    public Task OpenRecordingsFolderAsync()
    {
        return OpenRecordingsFolderAsync(CancellationToken.None);
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

    public Task<SettingsSaveResult> SaveSettingsAsync(PullWatchSettings settings)
    {
        return SaveSettingsAsync(settings, CancellationToken.None);
    }

    public Task<SettingsSaveResult> SaveSettingsAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UpdateSettingsCoreAsync(_ => settings, cancellationToken);
    }

    public Task<SettingsSaveResult> UpdateSettingsAsync(
        Func<PullWatchSettings, PullWatchSettings> updateSettings
    )
    {
        return UpdateSettingsAsync(updateSettings, CancellationToken.None);
    }

    public Task<SettingsSaveResult> UpdateSettingsAsync(
        Func<PullWatchSettings, PullWatchSettings> updateSettings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(updateSettings);
        return UpdateSettingsCoreAsync(updateSettings, cancellationToken);
    }

    private async Task<SettingsSaveResult> UpdateSettingsCoreAsync(
        Func<PullWatchSettings, PullWatchSettings> updateSettings,
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
            var settings = updateSettings(previousSettings);
            ArgumentNullException.ThrowIfNull(settings);
            var result = await settingsService.SaveAsync(settings, cancellationToken);

            if (!result.WasPersisted)
            {
                return result;
            }

            var savedSettings = result.Settings!;
            var videoEncoding = await EvaluateVideoEncodingAfterSaveAsync(
                previousSettings,
                savedSettings,
                cancellationToken
            );
            UpdateStatus(status =>
                status with
                {
                    EffectiveSettings = savedSettings,
                    Recording = ClearVideoEncodingSetupFailureAfterSave(
                        status.Recording,
                        previousSettings,
                        savedSettings,
                        videoEncoding
                    ),
                    VideoEncoding = videoEncoding,
                }
            );

            if (StorageSettingsChanged(previousSettings, savedSettings))
            {
                _recordingStorageCoordinator.QueueRefreshOrRetention(savedSettings);
            }

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

    public Task<SettingsSaveResult> SaveUiSettingsAsync(UiSettings uiSettings)
    {
        return SaveUiSettingsAsync(uiSettings, CancellationToken.None);
    }

    public Task<SettingsSaveResult> SaveUiSettingsAsync(
        UiSettings uiSettings,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(uiSettings);
        return UpdateUiSettingsAsync(_ => uiSettings, cancellationToken);
    }

    public Task<SettingsSaveResult> UpdateUiSettingsAsync(Func<UiSettings, UiSettings> updateUi)
    {
        return UpdateUiSettingsAsync(updateUi, CancellationToken.None);
    }

    public async Task<SettingsSaveResult> UpdateUiSettingsAsync(
        Func<UiSettings, UiSettings> updateUi,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(updateUi);

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
            var uiSettings = updateUi(settingsService.Current.Ui);
            ArgumentNullException.ThrowIfNull(uiSettings);
            var result = await settingsService.SaveAsync(
                settingsService.Current with
                {
                    Ui = uiSettings,
                },
                cancellationToken
            );

            if (result.WasPersisted)
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
        await _recordingStorageCoordinator.DisposeAsync();
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
        _recordingStorageCoordinator.ResetStatus();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    private static bool StorageSettingsChanged(
        PullWatchSettings previousSettings,
        PullWatchSettings currentSettings
    )
    {
        return !PathsEqual(
                previousSettings.RecordingsDirectory,
                currentSettings.RecordingsDirectory
            )
            || previousSettings.Storage != currentSettings.Storage;
    }

    private static RecordingCoordinatorStatus ClearVideoEncodingSetupFailureAfterSave(
        RecordingCoordinatorStatus recording,
        PullWatchSettings previousSettings,
        PullWatchSettings savedSettings,
        EncoderCalibrationStatus videoEncoding
    )
    {
        if (
            recording.LastFailure is null
            || !VideoEncodingSetupFailureClassifier.IsSetupFailure(recording.LastFailure)
            || !VideoEncodingSetupSettingsChanged(previousSettings, savedSettings)
            || !videoEncoding.IsValid
        )
        {
            return recording;
        }

        return recording with
        {
            LastFailure = null,
        };
    }

    private static bool VideoEncodingSetupSettingsChanged(
        PullWatchSettings previousSettings,
        PullWatchSettings currentSettings
    )
    {
        return previousSettings.Video.SelectedProfile != currentSettings.Video.SelectedProfile
            || previousSettings.EncoderCalibration != currentSettings.EncoderCalibration;
    }

    private async Task<EncoderCalibrationStatus> EvaluateVideoEncodingAfterSaveAsync(
        PullWatchSettings previousSettings,
        PullWatchSettings savedSettings,
        CancellationToken cancellationToken
    )
    {
        if (!VideoEncodingSetupSettingsChanged(previousSettings, savedSettings))
        {
            return Status.VideoEncoding!;
        }

        var environment = await _resolveEncoderCalibrationEnvironment(cancellationToken);
        return EncoderCalibrationStatusEvaluator.Evaluate(savedSettings, environment);
    }

    private void OnRecordingStatusChanged(RecordingCoordinatorStatus status)
    {
        var previousSavedCount = Status.Recording.Statistics.SavedCount;
        UpdateStatus(current => current with { Recording = status });

        if (status.Statistics.SavedCount <= previousSavedCount)
        {
            return;
        }

        var settings = _settingsService?.Current ?? Status.EffectiveSettings;

        if (settings is not null)
        {
            _recordingStorageCoordinator.QueueRefreshOrRetention(settings);
        }
    }

    private void UpdateStatus(Func<ApplicationStatus, ApplicationStatus> update)
    {
        _statusPublisher.Update(update);
    }
}
