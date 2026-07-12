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
    public void RejectsEntireSettingsObjectWhenVideoCodecIsInvalid()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Video = new VideoSettings
                {
                    SelectedProfile = new VideoProfileSelection
                    {
                        Codec = (VideoCodec)999,
                        Provider = VideoEncoderProvider.Software,
                    },
                },
            }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains("Selected video profile codec must be H.264 or H.265.", result.Errors);
    }

    [Fact]
    public void RejectsEntireSettingsObjectWhenVideoEncoderIsInvalid()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Video = new VideoSettings
                {
                    SelectedProfile = new VideoProfileSelection
                    {
                        Codec = VideoCodec.H264,
                        Provider = (VideoEncoderProvider)999,
                    },
                },
            }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains(
            "Selected video profile encoder must be NVIDIA NVENC, AMD AMF, or Software.",
            result.Errors
        );
    }

    [Fact]
    public void RejectsEntireSettingsObjectWhenCalibrationResultProfileIsInvalid()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                EncoderCalibration = new EncoderCalibrationSettings
                {
                    Results =
                    [
                        new EncoderCalibrationResult
                        {
                            Codec = VideoCodec.H264,
                            Provider = (VideoEncoderProvider)999,
                        },
                    ],
                },
            }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains(
            "Encoder calibration result encoder must be NVIDIA NVENC, AMD AMF, or Software.",
            result.Errors
        );
    }

    [Fact]
    public void RejectsEntireSettingsObjectWhenVideoScalingIsInvalid()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings { Video = new VideoSettings { Scaling = (VideoScaling)999 } }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains("Video scaling must be Original, 1440p, 1080p, or 720p.", result.Errors);
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
    public void RejectsNegativeMinimumKeystoneLevel()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                RecordingFilters = new RecordingFilterSettings
                {
                    MythicPlus = new MythicPlusRecordingFilterSettings
                    {
                        MinimumKeystoneLevel = -1,
                    },
                },
            }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains("Minimum Mythic+ keystone level cannot be negative.", result.Errors);
    }

    [Fact]
    public void RejectsNegativeRecordingStorageLimit()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings { Storage = new RecordingStorageSettings { MaxUsageBytes = -1 } }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains("Recording storage limit cannot be negative.", result.Errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsPlaybackVolumeOutsidePercentageRange(int volumePercent)
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings { Ui = new UiSettings { PlaybackVolumePercent = volumePercent } }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains("Playback volume must be between 0 and 100 percent.", result.Errors);
    }

    [Fact]
    public void ZeroPlaybackVolumeIsNormalizedToMuted()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Ui = new UiSettings { PlaybackVolumePercent = 0, IsPlaybackMuted = false },
            }
        );

        Assert.True(result.IsValid);
        Assert.True(result.Settings!.Ui.IsPlaybackMuted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositiveLastEnabledRecordingStorageLimit(long lastEnabledMaxUsageBytes)
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Storage = new RecordingStorageSettings
                {
                    LastEnabledMaxUsageBytes = lastEnabledMaxUsageBytes,
                },
            }
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains("Last enabled recording storage limit must be positive.", result.Errors);
    }

    [Fact]
    public void ActiveRecordingStorageLimitBecomesLastEnabledLimit()
    {
        var maxUsageBytes = 40L * 1024 * 1024 * 1024;
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Storage = new RecordingStorageSettings
                {
                    MaxUsageBytes = maxUsageBytes,
                    LastEnabledMaxUsageBytes = 10L * 1024 * 1024 * 1024,
                },
            }
        );

        Assert.True(result.IsValid);
        Assert.Equal(maxUsageBytes, result.Settings!.Storage.LastEnabledMaxUsageBytes);
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
    public void DisablesMicrophoneCapture()
    {
        var result = SettingsValidator.Validate(
            new PullWatchSettings
            {
                Audio = new AudioSettings { CaptureSystemAudio = false, CaptureMicrophone = true },
            }
        );

        Assert.True(result.IsValid);
        Assert.False(result.Settings!.Audio.CaptureSystemAudio);
        Assert.False(result.Settings.Audio.CaptureMicrophone);
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
