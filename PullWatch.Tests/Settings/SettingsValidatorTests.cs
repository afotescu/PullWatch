namespace PullWatch.Tests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void AppliesDefaultRecordingsDirectory()
    {
        var result = SettingsValidator.Validate(new PullWatchSettings());

        Assert.True(result.IsValid);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "PullWatch"
            ),
            result.Settings!.RecordingsDirectory
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(120)]
    public void RejectsEntireSettingsObjectWhenFrameRateIsInvalid(int frameRate)
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings { Video = new VideoSettings { FrameRate = frameRate } }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void RejectsEntireSettingsObjectWhenQualityIsInvalid()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings { Video = new VideoSettings { Quality = (VideoQuality)999 } }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void RejectsEntireSettingsObjectWhenSelectedRecordingCategoryIsInvalid()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Ui = new UiSettings { SelectedRecordingCategory = (RecordingListCategory)999 },
            }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ClearsStartMinimizedToTrayWhenWindowsStartupIsDisabled()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings { Startup = new StartupSettings { StartMinimizedToTray = true } }
        );

        Assert.True(result.IsValid);
        Assert.False(result.Settings!.Startup.StartMinimizedToTray);
    }

    [Fact]
    public void AllowsConfiguredLogsDirectoryToBeTemporarilyUnavailable()
    {
        var unavailablePath = Path.Combine(
            Path.GetTempPath(),
            $"PullWatch-Missing-{Guid.NewGuid():N}"
        );

        var result = SettingsValidator.Validate(
            new PullWatchSettings { WowLogsDirectory = unavailablePath }
        );

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(unavailablePath), result.Settings!.WowLogsDirectory);
        Assert.False(Directory.Exists(unavailablePath));
    }

    [Fact]
    public void ValidationNormalizesRecordingsDirectoryWithoutCreatingIt()
    {
        using var directory = new TemporaryDirectory();
        var recordingsDirectory = Path.Combine(directory.Path, "Missing", "Recordings");

        var result = SettingsValidator.Validate(
            new PullWatchSettings { RecordingsDirectory = recordingsDirectory }
        );

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(recordingsDirectory), result.Settings!.RecordingsDirectory);
        Assert.False(Directory.Exists(recordingsDirectory));
    }

    [Fact]
    public void RecordingsDirectoryProbeReportsUnwritableDirectorySeparately()
    {
        using var directory = new TemporaryDirectory();
        var blockedPath = Path.Combine(directory.Path, "recordings");
        File.WriteAllText(blockedPath, "not a directory");
        var validation = SettingsValidator.Validate(
            new PullWatchSettings { RecordingsDirectory = blockedPath }
        );

        Assert.True(validation.IsValid);

        var errors = SettingsValidator.ValidateRecordingsDirectoryWritable(validation.Settings!);

        var error = Assert.Single(errors);
        Assert.StartsWith("Recordings directory is not writable:", error);
    }

    [Fact]
    public void ProviderKeepsPreviousSnapshotWhenUpdateIsInvalid()
    {
        var original = SettingsValidator.Validate(new PullWatchSettings()).Settings!;
        var provider = new SettingsProvider(original);

        var result = provider.TryUpdate(
            original with
            {
                Video = original.Video with { FrameRate = 0 },
            }
        );

        Assert.False(result.IsValid);
        Assert.Same(original, provider.Current);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchSettingsValidatorTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
