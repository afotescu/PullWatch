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

        var effective = await bootstrapper.LoadEffectiveAsync(cancellationToken);
        var persisted = await store.LoadAsync(cancellationToken);

        Assert.NotNull(effective);
        Assert.Equal(SettingsLoadStatus.Loaded, persisted.Status);
        Assert.Equal(Path.GetFullPath(detectedLogsDirectory), persisted.Settings!.WowLogsDirectory);
        Assert.Equal(new VideoSettings(), persisted.Settings.Video);
        Assert.Equal(new AudioSettings(), persisted.Settings.Audio);
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

        var effective = await bootstrapper.LoadEffectiveAsync(cancellationToken);

        Assert.NotNull(effective);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(path, cancellationToken));
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
