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
            }
        );

        Assert.NotNull(controller.Status.EffectiveSettings);
        Assert.False(controller.StartedWithCreatedSettingsFile);
        Assert.Equal(CombatLogReaderState.WaitingForWow, controller.Status.CombatLog.State);
        Assert.False(monitorCreated);
    }

    [Fact]
    public async Task StartupReportsWhenSettingsFileWasCreated()
    {
        using var directory = new TemporaryDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        var bootstrapper = new SettingsBootstrapper(
            store,
            NullLogger<SettingsBootstrapper>.Instance,
            () => null
        );
        await using var controller = new ApplicationController(
            bootstrapper,
            _ => new FakeRecordingService(),
            _ => new FakeCombatLogMonitor(),
            NullLoggerFactory.Instance
        );

        await controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(controller.StartedWithCreatedSettingsFile);
        Assert.True(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task StartupWithLogsDirectoryStartsMonitoringAndAggregatesStatus()
    {
        using var directory = new TemporaryDirectory();
        var logsDirectory = Directory
            .CreateDirectory(Path.Combine(directory.Path, "Logs"))
            .FullName;
        var monitor = new FakeCombatLogMonitor();
        var wowMonitor = AvailableWowMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            logsDirectory,
            new FakeRecordingService(),
            _ => monitor,
            () => wowMonitor
        );
        var status = new CombatLogReaderStatus(
            CombatLogReaderState.ReadingCombatLog,
            Path.Combine(logsDirectory, "WoWCombatLog-test.txt"),
            DateTimeOffset.UtcNow,
            null
        );

        await WaitForAsync(() => monitor.Started);
        monitor.Publish(status);
        await WaitForAsync(() => controller.Status.CombatLog == status);

        Assert.True(monitor.Started);
        Assert.Equal(status, controller.Status.CombatLog);
    }

    [Fact]
    public async Task UnexpectedCombatLogMonitoringFailureReportsInactiveError()
    {
        using var directory = new TemporaryDirectory();
        var logsDirectory = Directory
            .CreateDirectory(Path.Combine(directory.Path, "Logs"))
            .FullName;
        var exception = new InvalidOperationException("Reader failed.");
        var monitor = new FakeCombatLogMonitor { ReadException = exception };
        monitor.Publish(
            new CombatLogReaderStatus(
                CombatLogReaderState.ReadingCombatLog,
                Path.Combine(logsDirectory, "WoWCombatLog-test.txt"),
                DateTimeOffset.UtcNow,
                null
            )
        );
        var wowMonitor = AvailableWowMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            logsDirectory,
            new FakeRecordingService(),
            _ => monitor,
            () => wowMonitor
        );

        await WaitForAsync(() => controller.Status.CombatLog.LastFileSystemError == exception);

        Assert.Equal(CombatLogReaderState.WaitingForCombatLog, controller.Status.CombatLog.State);
        Assert.Same(exception, controller.Status.CombatLog.LastFileSystemError);
    }

    [Fact]
    public async Task StartupStartsWowProcessMonitoringAndAggregatesStatus()
    {
        using var directory = new TemporaryDirectory();
        var wowMonitor = new FakeWowProcessMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            null,
            new FakeRecordingService(),
            _ => new FakeCombatLogMonitor(),
            () => wowMonitor
        );
        var status = new WowProcessStatus(
            WowProcessState.WindowAvailable,
            1234,
            "World of Warcraft",
            null
        );

        await WaitForAsync(() => wowMonitor.Started);
        wowMonitor.Publish(status);
        await WaitForAsync(() => controller.Status.WowProcess == status);

        Assert.Equal(status, controller.Status.WowProcess);
    }

    [Fact]
    public async Task CombatLogMonitoringStopsWhenWowWindowBecomesUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var logsDirectory = Directory
            .CreateDirectory(Path.Combine(directory.Path, "Logs"))
            .FullName;
        var monitor = new FakeCombatLogMonitor();
        var wowMonitor = AvailableWowMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            logsDirectory,
            new FakeRecordingService(),
            _ => monitor,
            () => wowMonitor
        );

        await WaitForAsync(() => monitor.Started);
        wowMonitor.Publish(
            new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null)
        );
        await WaitForAsync(() => monitor.Stopped);
        await WaitForAsync(() =>
            controller.Status.CombatLog.State == CombatLogReaderState.WaitingForWow
        );
    }

    [Fact]
    public async Task ManualCommandsExposeActivePathAndClearItWhenStopped()
    {
        using var directory = new TemporaryDirectory();
        var recorder = new FakeRecordingService
        {
            ActiveOutputPath = Path.Combine(directory.Path, "manual.mp4"),
        };
        await using var controller = await CreateControllerAsync(
            directory.Path,
            null,
            recorder,
            _ => new FakeCombatLogMonitor()
        );

        Assert.Equal(
            RecordingCommandResult.Started,
            await controller.StartManualRecordingAsync(CancellationToken.None)
        );
        await WaitForAsync(() =>
            controller.Status.Recording.State == RecordingCoordinatorState.Recording
        );

        Assert.Equal(recorder.ActiveOutputPath, controller.Status.Recording.ActiveOutputPath);

        Assert.Equal(
            RecordingCommandResult.Stopped,
            await controller.StopManualRecordingAsync(CancellationToken.None)
        );
        await WaitForAsync(() =>
            controller.Status.Recording.State == RecordingCoordinatorState.Idle
        );

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
            _ => new FakeCombatLogMonitor()
        );

        await controller.StartManualRecordingAsync(CancellationToken.None);
        await controller.ShutdownAsync(CancellationToken.None);
        await controller.ShutdownAsync(CancellationToken.None);

        Assert.Equal(["start", "stop"], recorder.Calls);
    }

    [Fact]
    public async Task SavesSettingsAndRestartsMonitoringWhenLogsDirectoryChanges()
    {
        using var directory = new TemporaryDirectory();
        var firstLogsDirectory = Directory
            .CreateDirectory(Path.Combine(directory.Path, "FirstLogs"))
            .FullName;
        var secondLogsDirectory = Directory
            .CreateDirectory(Path.Combine(directory.Path, "SecondLogs"))
            .FullName;
        var monitors = new List<(string Path, FakeCombatLogMonitor Monitor)>();
        var wowMonitor = AvailableWowMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            firstLogsDirectory,
            new FakeRecordingService(),
            path =>
            {
                var monitor = new FakeCombatLogMonitor();
                monitors.Add((path, monitor));
                return monitor;
            },
            () => wowMonitor
        );

        var result = await controller.SaveSettingsAsync(
            controller.Status.EffectiveSettings! with
            {
                WowLogsDirectory = secondLogsDirectory,
                Video = controller.Status.EffectiveSettings!.Video with
                {
                    Quality = VideoQuality.High,
                    FrameRate = VideoFrameRates.Standard,
                },
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.Saved, result.Status);
        Assert.Equal(VideoQuality.High, controller.Status.EffectiveSettings!.Video.Quality);
        Assert.Equal(
            VideoFrameRates.Standard,
            controller.Status.EffectiveSettings!.Video.FrameRate
        );
        Assert.Equal(2, monitors.Count);
        Assert.Equal(firstLogsDirectory, monitors[0].Path);
        Assert.Equal(secondLogsDirectory, monitors[1].Path);
        Assert.True(monitors[0].Monitor.Stopped);
        await WaitForAsync(() => monitors[1].Monitor.Started);
    }

    [Fact]
    public async Task ClearingLogsDirectoryStopsMonitoring()
    {
        using var directory = new TemporaryDirectory();
        var logsDirectory = Directory
            .CreateDirectory(Path.Combine(directory.Path, "Logs"))
            .FullName;
        var monitor = new FakeCombatLogMonitor();
        var wowMonitor = AvailableWowMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            logsDirectory,
            new FakeRecordingService(),
            _ => monitor,
            () => wowMonitor
        );

        var result = await controller.SaveSettingsAsync(
            controller.Status.EffectiveSettings! with
            {
                WowLogsDirectory = null,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.Saved, result.Status);
        Assert.True(monitor.Stopped);
        Assert.Null(controller.Status.EffectiveSettings!.WowLogsDirectory);
        Assert.Equal(
            CombatLogReaderState.WaitingForLogsDirectory,
            controller.Status.CombatLog.State
        );
    }

    [Fact]
    public async Task RejectsSettingsSaveWhileRecording()
    {
        using var directory = new TemporaryDirectory();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            null,
            new FakeRecordingService(),
            _ => new FakeCombatLogMonitor()
        );
        await controller.StartManualRecordingAsync(TestContext.Current.CancellationToken);
        var original = controller.Status.EffectiveSettings!;

        var result = await controller.SaveSettingsAsync(
            original with
            {
                RecordMythicPlus = false,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.RecordingActive, result.Status);
        Assert.Equal(original, controller.Status.EffectiveSettings);
    }

    [Fact]
    public async Task SavesUiSettingsWhileRecording()
    {
        using var directory = new TemporaryDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        await store.SaveAsync(
            new PullWatchSettings
            {
                RecordingsDirectory = Path.Combine(directory.Path, "Recordings"),
            },
            TestContext.Current.CancellationToken
        );
        var bootstrapper = new SettingsBootstrapper(
            store,
            NullLogger<SettingsBootstrapper>.Instance,
            () => null
        );
        await using var controller = new ApplicationController(
            bootstrapper,
            _ => new FakeRecordingService(),
            _ => new FakeCombatLogMonitor(),
            NullLoggerFactory.Instance
        );
        await controller.StartAsync(TestContext.Current.CancellationToken);
        await controller.StartManualRecordingAsync(TestContext.Current.CancellationToken);
        var ui = new UiSettings
        {
            WindowPlacement = new WindowPlacementSettings
            {
                Left = 25,
                Top = 50,
                Width = 1200,
                Height = 800,
                IsMaximized = true,
            },
        };

        var result = await controller.SaveUiSettingsAsync(
            ui,
            TestContext.Current.CancellationToken
        );
        var persisted = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsSaveStatus.Saved, result.Status);
        Assert.Equal(ui, controller.Status.EffectiveSettings!.Ui);
        Assert.Equal(ui, persisted.Settings!.Ui);
    }

    [Fact]
    public async Task StatusNotificationsDoNotUsePublishersSynchronizationContext()
    {
        using var directory = new TemporaryDirectory();
        var monitor = new FakeCombatLogMonitor();
        var wowMonitor = AvailableWowMonitor();
        await using var controller = await CreateControllerAsync(
            directory.Path,
            Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName,
            new FakeRecordingService(),
            _ => monitor,
            () => wowMonitor
        );
        var publisherContext = new SynchronizationContext();
        var callbackContext = new TaskCompletionSource<SynchronizationContext?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        controller.StatusChanged += _ =>
            callbackContext.TrySetResult(SynchronizationContext.Current);
        var previousContext = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(publisherContext);
            monitor.Publish(
                new CombatLogReaderStatus(
                    CombatLogReaderState.ReadingCombatLog,
                    "test",
                    DateTimeOffset.UtcNow,
                    null
                )
            );
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.NotSame(
            publisherContext,
            await callbackContext.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken
            )
        );
    }

    private static async Task<ApplicationController> CreateControllerAsync(
        string rootDirectory,
        string? logsDirectory,
        FakeRecordingService recorder,
        Func<string, ICombatLogMonitor> createMonitor,
        Func<IWowProcessMonitor>? createWowProcessMonitor = null
    )
    {
        var store = new SettingsStore(Path.Combine(rootDirectory, "settings.json"));
        await store.SaveAsync(
            new PullWatchSettings
            {
                WowLogsDirectory = logsDirectory,
                RecordingsDirectory = Path.Combine(rootDirectory, "Recordings"),
            },
            TestContext.Current.CancellationToken
        );
        var bootstrapper = new SettingsBootstrapper(
            store,
            NullLogger<SettingsBootstrapper>.Instance,
            () => null
        );
        var controller = new ApplicationController(
            bootstrapper,
            _ => recorder,
            createMonitor,
            NullLoggerFactory.Instance,
            createWowProcessMonitor
        );
        await controller.StartAsync(TestContext.Current.CancellationToken);
        return controller;
    }

    private static FakeWowProcessMonitor AvailableWowMonitor()
    {
        return new FakeWowProcessMonitor(
            new WowProcessStatus(WowProcessState.WindowAvailable, 1234, "World of Warcraft", null)
        );
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
