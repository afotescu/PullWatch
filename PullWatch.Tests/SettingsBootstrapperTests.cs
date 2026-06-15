using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class SettingsBootstrapperTests
{
    [Fact]
    public async Task MissingFileIsCreatedWithDefaultsAndDetectedLogsDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var detectedLogsDirectory = Path.Combine(directory.Path, "Wow", "Logs");
        var store = new SettingsStore(path);
        var bootstrapper = new SettingsBootstrapper(
            store,
            NullLogger<SettingsBootstrapper>.Instance,
            () => detectedLogsDirectory);

        var effective = await bootstrapper.LoadEffectiveAsync(
            EmptyCommandLine(),
            cancellationToken);
        var persisted = await store.LoadAsync(cancellationToken);

        Assert.NotNull(effective);
        Assert.Equal(SettingsLoadStatus.Loaded, persisted.Status);
        Assert.Equal(Path.GetFullPath(detectedLogsDirectory), persisted.Settings!.WowLogsDirectory);
        Assert.Equal(new VideoSettings(), persisted.Settings.Video);
        Assert.Equal(new AudioSettings(), persisted.Settings.Audio);
    }

    [Fact]
    public async Task CliOverridesAreNotWrittenToNewSettingsFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var persistedLogsDirectory = Path.Combine(directory.Path, "DetectedLogs");
        var overrideLogsDirectory = Path.Combine(directory.Path, "OverrideLogs");
        var store = new SettingsStore(path);
        var bootstrapper = new SettingsBootstrapper(
            store,
            NullLogger<SettingsBootstrapper>.Instance,
            () => persistedLogsDirectory);
        var commandLine = EmptyCommandLine() with
        {
            WowLogsDirectory = overrideLogsDirectory,
            RecordMythicPlus = false
        };

        var effective = await bootstrapper.LoadEffectiveAsync(commandLine, cancellationToken);
        var persisted = await store.LoadAsync(cancellationToken);

        Assert.Equal(Path.GetFullPath(overrideLogsDirectory), effective!.WowLogsDirectory);
        Assert.False(effective.RecordMythicPlus);
        Assert.Equal(Path.GetFullPath(persistedLogsDirectory), persisted.Settings!.WowLogsDirectory);
        Assert.True(persisted.Settings.RecordMythicPlus);
    }

    [Fact]
    public async Task InvalidExistingFileIsNotOverwritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        const string invalidJson = """{ "Version": 1, "Video": { "FrameRate": 0 } }""";
        await File.WriteAllTextAsync(path, invalidJson, cancellationToken);
        var bootstrapper = new SettingsBootstrapper(
            new SettingsStore(path),
            NullLogger<SettingsBootstrapper>.Instance,
            () => null);

        var effective = await bootstrapper.LoadEffectiveAsync(
            EmptyCommandLine(),
            cancellationToken);

        Assert.NotNull(effective);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static CommandLineOptions EmptyCommandLine()
    {
        return new CommandLineOptions(false, null, null, null, null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchBootstrapperTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
