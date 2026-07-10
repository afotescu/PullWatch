using Microsoft.Extensions.Logging.Abstractions;
using PullWatch.Tests.TestDoubles;

namespace PullWatch.Tests;

internal sealed class ApplicationControllerTestBuilder(SettingsBootstrapper settingsBootstrapper)
{
    internal static EncoderCalibrationEnvironment DefaultEncoderCalibrationEnvironment { get; } =
        new(@"C:\ffmpeg\bin\ffmpeg.exe", "ffmpeg version test", "ABC123");

    private Func<SettingsProvider, IRecordingService> _createRecordingService =
        _ => new FakeRecordingService();
    private Func<string, DateTimeOffset?, Func<bool>, ICombatLogMonitor> _createCombatLogMonitor =
        CreateDefaultCombatLogMonitor;
    private Func<IWowProcessMonitor> _createWowProcessMonitor = () => new FakeWowProcessMonitor();
    private Func<
        CancellationToken,
        Task<EncoderCalibrationEnvironment>
    > _resolveEncoderCalibrationEnvironment = _ =>
        Task.FromResult(DefaultEncoderCalibrationEnvironment);
    private IRecordingStorageInitializer? _storageInitializer;

    private static ICombatLogMonitor CreateDefaultCombatLogMonitor(
        string logsDirectory,
        DateTimeOffset? wowProcessStartedAtUtc,
        Func<bool> canDiscoverCombatLog
    )
    {
        return new FakeCombatLogMonitor();
    }

    public ApplicationControllerTestBuilder WithRecordingService(IRecordingService recorder)
    {
        _createRecordingService = _ => recorder;
        return this;
    }

    public ApplicationControllerTestBuilder WithRecordingServiceFactory(
        Func<SettingsProvider, IRecordingService> createRecordingService
    )
    {
        _createRecordingService = createRecordingService;
        return this;
    }

    public ApplicationControllerTestBuilder WithCombatLogMonitorFactory(
        Func<string, ICombatLogMonitor> createCombatLogMonitor
    )
    {
        _createCombatLogMonitor = (logsDirectory, _, _) => createCombatLogMonitor(logsDirectory);
        return this;
    }

    public ApplicationControllerTestBuilder WithCombatLogDiscoveryFactory(
        Func<string, Func<bool>, ICombatLogMonitor> createCombatLogMonitor
    )
    {
        _createCombatLogMonitor = (logsDirectory, _, canDiscoverCombatLog) =>
            createCombatLogMonitor(logsDirectory, canDiscoverCombatLog);
        return this;
    }

    public ApplicationControllerTestBuilder WithCombatLogSessionFactory(
        Func<string, DateTimeOffset?, Func<bool>, ICombatLogMonitor> createCombatLogMonitor
    )
    {
        _createCombatLogMonitor = createCombatLogMonitor;
        return this;
    }

    public ApplicationControllerTestBuilder WithWowProcessMonitor(IWowProcessMonitor monitor)
    {
        _createWowProcessMonitor = () => monitor;
        return this;
    }

    public ApplicationControllerTestBuilder WithWowProcessMonitorFactory(
        Func<IWowProcessMonitor> createWowProcessMonitor
    )
    {
        _createWowProcessMonitor = createWowProcessMonitor;
        return this;
    }

    public ApplicationControllerTestBuilder WithStorageInitializer(
        IRecordingStorageInitializer storageInitializer
    )
    {
        _storageInitializer = storageInitializer;
        return this;
    }

    public ApplicationControllerTestBuilder WithEncoderCalibrationEnvironmentResolver(
        Func<CancellationToken, Task<EncoderCalibrationEnvironment>> resolveEnvironment
    )
    {
        _resolveEncoderCalibrationEnvironment = resolveEnvironment;
        return this;
    }

    public ApplicationController Build()
    {
        return new ApplicationController(
            settingsBootstrapper,
            _createRecordingService,
            _createCombatLogMonitor,
            NullLoggerFactory.Instance,
            _createWowProcessMonitor,
            storageInitializer: _storageInitializer,
            resolveEncoderCalibrationEnvironment: _resolveEncoderCalibrationEnvironment
        );
    }
}
