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
}
