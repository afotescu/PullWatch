namespace PullWatch.Tests;

public sealed class ApplicationSettingsServiceTests
{
    [Fact]
    public async Task PersistenceFailureLeavesProviderUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var original = SettingsValidator.Validate(new PullWatchSettings
        {
            RecordingsDirectory = directory.Path
        }).Settings!;
        var provider = new SettingsProvider(original);
        var store = new SettingsStore(
            Path.Combine(directory.Path, "settings.json"),
            (_, _) => throw new IOException("Simulated save failure."));
        var service = new ApplicationSettingsService(store, provider);

        var result = await service.SaveAsync(
            original with { RecordMythicPlus = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(SettingsSaveStatus.PersistenceFailed, result.Status);
        Assert.Same(original, provider.Current);
    }

    [Fact]
    public async Task InvalidSettingsNeitherPersistNorApply()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var original = SettingsValidator.Validate(new PullWatchSettings
        {
            RecordingsDirectory = directory.Path
        }).Settings!;
        var provider = new SettingsProvider(original);
        var service = new ApplicationSettingsService(new SettingsStore(settingsPath), provider);

        var result = await service.SaveAsync(
            original with { Video = original.Video with { FrameRate = 0 } },
            TestContext.Current.CancellationToken);

        Assert.Equal(SettingsSaveStatus.Invalid, result.Status);
        Assert.Same(original, provider.Current);
        Assert.False(File.Exists(settingsPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchApplicationSettingsTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
