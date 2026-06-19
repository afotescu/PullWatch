namespace PullWatch.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task ReturnsMissingWhenFileDoesNotExist()
    {
        using var directory = new TemporaryDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"));

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task RoundTripsSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new SettingsStore(path);
        var settings = new PullWatchSettings
        {
            WowLogsDirectory = @"D:\Games\World of Warcraft\_retail_\Logs",
            RecordingsDirectory = @"D:\Recordings",
            RecordMythicPlus = false,
            Video = new VideoSettings
            {
                Bitrate = 20_000_000,
                FrameRate = 120,
                CaptureCursor = false,
                ShowCaptureBorder = true,
            },
            Audio = new AudioSettings { CaptureSystemAudio = false, CaptureMicrophone = true },
        };

        await store.SaveAsync(settings, CancellationToken.None);
        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(settings, result.Settings);
    }

    [Fact]
    public async Task MalformedFileIsRejectedAndLeftUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        const string malformedJson = """{ "Version": nope }""";
        await File.WriteAllTextAsync(path, malformedJson, cancellationToken);
        var store = new SettingsStore(path);

        var result = await store.LoadAsync(cancellationToken);

        Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(malformedJson, await File.ReadAllTextAsync(path, cancellationToken));
    }

    [Fact]
    public async Task UnknownJsonPropertiesAreTolerated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{ "Version": 1, "FutureSetting": true }""",
            cancellationToken
        );
        var store = new SettingsStore(path);

        var result = await store.LoadAsync(cancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.NotNull(result.Settings);
    }

    [Fact]
    public async Task FailedAtomicReplacementPreservesExistingSettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        const string existingJson = """{ "Version": 1, "RecordMythicPlus": false }""";
        await File.WriteAllTextAsync(path, existingJson, cancellationToken);
        var store = new SettingsStore(
            path,
            (_, _) => throw new IOException("Simulated replace failure.")
        );

        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(new PullWatchSettings(), cancellationToken)
        );

        Assert.Equal(existingJson, await File.ReadAllTextAsync(path, cancellationToken));
        Assert.Single(Directory.EnumerateFiles(directory.Path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchSettingsTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
