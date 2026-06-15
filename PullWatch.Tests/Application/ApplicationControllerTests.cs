using Microsoft.Extensions.Logging.Abstractions;
using PullWatch.Tests.TestDoubles;

namespace PullWatch.Tests;

public sealed class ApplicationControllerTests
{
    [Fact]
    public async Task StartupWithoutLogsDirectoryKeepsApplicationRunningAndReportsWaiting()
    {
        using var directory = new TemporaryDirectory();
        var recorder = new FakeRecordingService();
        var monitorCreated = false;
        await using var controller = await CreateControllerAsync(
            directory.Path,
            null,
            recorder,
            _ =>
            {
                monitorCreated = true;
                return new FakeCombatLogMonitor();
            });

        Assert.NotNull(controller.Status.EffectiveSettings);
        Assert.Equal(
            CombatLogReaderState.WaitingForLogsDirectory,
            controller.Status.CombatLog.State);
        Assert.False(monitorCreated);
    }

    [Fact]
    public async Task StartupWithLogsDirectoryStartsMonitoringAndAggregatesStatus()
    {
        using var directory = new TemporaryDirectory();
        var logsDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "Logs")).FullName;
        var monitor = new FakeCombatLogMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            logsDirectory,
            new FakeRecordingService(),
            _ => monitor);
        var status = new CombatLogReaderStatus(
            CombatLogReaderState.ReadingCombatLog,
            Path.Combine(logsDirectory, "WoWCombatLog-test.txt"),
            DateTimeOffset.UtcNow,
            null);

        monitor.Publish(status);
        await WaitForAsync(() => controller.Status.CombatLog == status);

        Assert.True(monitor.Started);
        Assert.Equal(status, controller.Status.CombatLog);
    }

    [Fact]
    public async Task ManualCommandsExposeActivePathAndClearItWhenStopped()
    {
        using var directory = new TemporaryDirectory();
        var recorder = new FakeRecordingService
        {
            ActiveOutputPath = Path.Combine(directory.Path, "manual.mp4")
        };
        await using var controller = await CreateControllerAsync(
            directory.Path,
            null,
            recorder,
            _ => new FakeCombatLogMonitor());

        Assert.Equal(
            RecordingCommandResult.Started,
            await controller.StartManualRecordingAsync(CancellationToken.None));
        await WaitForAsync(
            () => controller.Status.Recording.State == RecordingCoordinatorState.Recording);

        Assert.Equal(recorder.ActiveOutputPath, controller.Status.Recording.ActiveOutputPath);

        Assert.Equal(
            RecordingCommandResult.Stopped,
            await controller.StopManualRecordingAsync(CancellationToken.None));
        await WaitForAsync(
            () => controller.Status.Recording.State == RecordingCoordinatorState.Idle);

        Assert.Null(controller.Status.Recording.ActiveOutputPath);
    }

    [Fact]
    public async Task ShutdownFinalizesActiveRecordingExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var recorder = new FakeRecordingService();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            null,
            recorder,
            _ => new FakeCombatLogMonitor());

        await controller.StartManualRecordingAsync(CancellationToken.None);
        await controller.ShutdownAsync(CancellationToken.None);
        await controller.ShutdownAsync(CancellationToken.None);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task StatusNotificationsDoNotUsePublishersSynchronizationContext()
    {
        using var directory = new TemporaryDirectory();
        var monitor = new FakeCombatLogMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName,
            new FakeRecordingService(),
            _ => monitor);
        var publisherContext = new SynchronizationContext();
        var callbackContext = new TaskCompletionSource<SynchronizationContext?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += _ => callbackContext.TrySetResult(SynchronizationContext.Current);
        var previousContext = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(publisherContext);
            monitor.Publish(new CombatLogReaderStatus(
                CombatLogReaderState.ReadingCombatLog,
                "test",
                DateTimeOffset.UtcNow,
                null));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.NotSame(
            publisherContext,
            await callbackContext.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
    }

    private static async Task<ApplicationController> CreateControllerAsync(
        string rootDirectory,
        string? logsDirectory,
        FakeRecordingService recorder,
        Func<string, ICombatLogMonitor> createMonitor)
    {
        var store = new SettingsStore(Path.Combine(rootDirectory, "settings.json"));
        await store.SaveAsync(
            new PullWatchSettings
            {
                WowLogsDirectory = logsDirectory,
                RecordingsDirectory = Path.Combine(rootDirectory, "Recordings")
            },
            TestContext.Current.CancellationToken);
        var bootstrapper = new SettingsBootstrapper(
            store,
            NullLogger<SettingsBootstrapper>.Instance,
            () => null);
        var controller = new ApplicationController(
            bootstrapper,
            _ => recorder,
            createMonitor,
            NullLoggerFactory.Instance);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        return controller;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchControllerTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
