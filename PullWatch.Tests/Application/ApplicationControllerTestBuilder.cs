using Microsoft.Extensions.Logging.Abstractions;
using PullWatch.Tests.TestDoubles;

namespace PullWatch.Tests;

internal sealed class ApplicationControllerTestBuilder(SettingsBootstrapper settingsBootstrapper)
{
    private Func<SettingsProvider, IRecordingService> _createRecordingService =
        _ => new FakeRecordingService();
    private Func<string, DateTimeOffset?, Func<bool>, ICombatLogMonitor> _createCombatLogMonitor =
        CreateDefaultCombatLogMonitor;
    private Func<IWowProcessMonitor> _createWowProcessMonitor = () => new FakeWowProcessMonitor();
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

    public ApplicationController Build()
    {
        return new ApplicationController(
            settingsBootstrapper,
            _createRecordingService,
            _createCombatLogMonitor,
            NullLoggerFactory.Instance,
            _createWowProcessMonitor,
            _storageInitializer
        );
    }
}
