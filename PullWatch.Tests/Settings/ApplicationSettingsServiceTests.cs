namespace PullWatch.Tests;

public sealed class ApplicationSettingsServiceTests
{
    [Fact]
    public async Task PersistenceFailureLeavesProviderUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var original = SettingsValidator
            .Validate(new PullWatchSettings { RecordingsDirectory = directory.Path })
            .Settings!;
        var provider = new SettingsProvider(original);
        var store = new SettingsStore(
            Path.Combine(directory.Path, "settings.json"),
            (_, _) => throw new IOException("Simulated save failure.")
        );
        var service = new ApplicationSettingsService(store, provider);

        var result = await service.SaveAsync(
            original with
            {
                RecordMythicPlus = false,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.PersistenceFailed, result.Status);
        Assert.Same(original, provider.Current);
    }

    [Fact]
    public async Task InvalidSettingsNeitherPersistNorApply()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var original = SettingsValidator
            .Validate(new PullWatchSettings { RecordingsDirectory = directory.Path })
            .Settings!;
        var provider = new SettingsProvider(original);
        var service = new ApplicationSettingsService(new SettingsStore(settingsPath), provider);

        var result = await service.SaveAsync(
            original with
            {
                Video = original.Video with { FrameRate = 0 },
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.Invalid, result.Status);
        Assert.Same(original, provider.Current);
        Assert.False(File.Exists(settingsPath));
    }

    [Fact]
    public async Task RecordingsDirectoryProbeFailureNeitherPersistsNorApplies()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var original = SettingsValidator
            .Validate(
                new PullWatchSettings
                {
                    RecordingsDirectory = Path.Combine(directory.Path, "original"),
                }
            )
            .Settings!;
        var blockedPath = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        var provider = new SettingsProvider(original);
        var service = new ApplicationSettingsService(new SettingsStore(settingsPath), provider);

        var result = await service.SaveAsync(
            original with
            {
                RecordingsDirectory = blockedPath,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.Invalid, result.Status);
        Assert.Same(original, provider.Current);
        Assert.False(File.Exists(settingsPath));
        Assert.Contains(
            result.ValidationErrors,
            error =>
                error.StartsWith("Recordings directory is not writable:", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task SaveAppliesPersistedSettingsWithoutSecondDirectoryProbe()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var original = SettingsValidator
            .Validate(
                new PullWatchSettings
                {
                    RecordingsDirectory = Path.Combine(directory.Path, "original"),
                }
            )
            .Settings!;
        var updatedDirectory = Path.Combine(directory.Path, "updated");
        var provider = new SettingsProvider(original);
        var store = new SettingsStore(
            settingsPath,
            (sourcePath, destinationPath) =>
            {
                File.Move(sourcePath, destinationPath);
                Directory.Delete(updatedDirectory, true);
                File.WriteAllText(updatedDirectory, "not a directory anymore");
            }
        );
        var service = new ApplicationSettingsService(store, provider);

        var result = await service.SaveAsync(
            original with
            {
                RecordingsDirectory = updatedDirectory,
                RecordMythicPlus = false,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SettingsSaveStatus.Saved, result.Status);
        Assert.Same(result.Settings, provider.Current);
        Assert.Equal(Path.GetFullPath(updatedDirectory), provider.Current.RecordingsDirectory);
        Assert.False(provider.Current.RecordMythicPlus);
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
