using ScreenRecorderLib;

namespace PullWatch.Tests;

public sealed class ScreenRecordingServiceTests
{
    [Theory]
    [InlineData(VideoQuality.Compact, 9_000_000)]
    [InlineData(VideoQuality.Balanced, 12_000_000)]
    [InlineData(VideoQuality.High, 18_000_000)]
    public void VideoEncoderOptionsUseOptimizedOutputSizeAndLongRecordingDefaults(
        VideoQuality quality,
        int expectedBitrate
    )
    {
        var settings = new PullWatchSettings
        {
            Video = new VideoSettings { Quality = quality, FrameRate = VideoFrameRates.High },
        };

        var options = ScreenRecordingService.CreateVideoEncoderOptions(
            settings,
            new VideoCaptureSize(2560, 1440)
        );
        var encoder = Assert.IsType<H264VideoEncoder>(options.Encoder);

        Assert.Equal(expectedBitrate, options.Bitrate);
        Assert.Equal(VideoFrameRates.High, options.Framerate);
        Assert.True(options.IsHardwareEncodingEnabled);
        Assert.False(options.IsLowLatencyEnabled);
        Assert.False(options.IsFixedFramerate);
        Assert.False(options.IsThrottlingDisabled);
        Assert.True(options.IsFragmentedMp4Enabled);
        Assert.False(options.IsMp4FastStartEnabled);
        Assert.Equal(H264BitrateControlMode.UnconstrainedVBR, encoder.BitrateMode);
    }

    [Theory]
    [InlineData(1920, 1080, 12_000_000)]
    [InlineData(2560, 1440, 12_000_000)]
    [InlineData(3440, 1440, 16_000_000)]
    [InlineData(3840, 2160, 12_000_000)]
    public void VideoEncoderOptionsUseOutputSizeWhenCalculatingBitrate(
        int width,
        int height,
        int expectedBitrate
    )
    {
        var settings = new PullWatchSettings
        {
            Video = new VideoSettings
            {
                Quality = VideoQuality.Balanced,
                FrameRate = VideoFrameRates.High,
            },
        };

        var options = ScreenRecordingService.CreateVideoEncoderOptions(
            settings,
            new VideoCaptureSize(width, height)
        );

        Assert.Equal(expectedBitrate, options.Bitrate);
    }

    [Theory]
    [InlineData(VideoScaling.Target1440p, 22_000_000)]
    [InlineData(VideoScaling.Target720p, 6_000_000)]
    public void VideoEncoderOptionsUseSelectedScalingTarget(
        VideoScaling scaling,
        int expectedBitrate
    )
    {
        var settings = new PullWatchSettings
        {
            Video = new VideoSettings
            {
                Quality = VideoQuality.Balanced,
                FrameRate = VideoFrameRates.High,
                Scaling = scaling,
            },
        };

        var options = ScreenRecordingService.CreateVideoEncoderOptions(
            settings,
            new VideoCaptureSize(3840, 2160)
        );

        Assert.Equal(expectedBitrate, options.Bitrate);
    }

    [Fact]
    public void OriginalScalingKeepsCaptureSizeWhenCalculatingBitrate()
    {
        var settings = new PullWatchSettings
        {
            Video = new VideoSettings
            {
                Quality = VideoQuality.Balanced,
                FrameRate = VideoFrameRates.High,
                Scaling = VideoScaling.Original,
            },
        };

        var options = ScreenRecordingService.CreateVideoEncoderOptions(
            settings,
            new VideoCaptureSize(2560, 1440)
        );

        Assert.Equal(22_000_000, options.Bitrate);
    }

    [Fact]
    public void CreateOptionsAppliesOptimizedOutputSizeToRecorderAndSource()
    {
        var source = new WindowRecordingSource(nint.Zero);

        var options = ScreenRecordingService.CreateOptions(
            source,
            new PullWatchSettings(),
            new VideoCaptureSize(2560, 1440)
        );

        Assert.Equal(1920, options.OutputOptions.OutputFrameSize.Width);
        Assert.Equal(1080, options.OutputOptions.OutputFrameSize.Height);
        Assert.Equal(StretchMode.Fill, options.OutputOptions.Stretch);
        Assert.Equal(1920, source.OutputSize.Width);
        Assert.Equal(1080, source.OutputSize.Height);
        Assert.Equal(StretchMode.Fill, source.Stretch);
    }

    [Fact]
    public void AudioOptionsUseExplicitStereoDefaults()
    {
        var settings = new PullWatchSettings
        {
            Audio = new AudioSettings { CaptureSystemAudio = true },
        };

        var options = ScreenRecordingService.CreateAudioOptions(settings);

        Assert.True(options.IsAudioEnabled);
        Assert.True(options.IsOutputDeviceEnabled);
        Assert.False(options.IsInputDeviceEnabled);
        Assert.Equal(AudioBitrate.bitrate_96kbps, options.Bitrate);
        Assert.Equal(AudioChannels.Stereo, options.Channels);
    }

    [Fact]
    public void CreateOutputPathReportsUnavailableRecordingsDirectory()
    {
        using var directory = new TemporaryDirectory();
        var blockedPath = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        var settings = new PullWatchSettings { RecordingsDirectory = blockedPath };

        var exception = Assert.Throws<RecordingOutputUnavailableException>(() =>
            ScreenRecordingService.CreateOutputPath(
                new ManualRecordingContext(DateTimeOffset.Now),
                settings
            )
        );

        Assert.Equal(Path.GetFullPath(blockedPath), exception.RecordingsDirectory);
        Assert.Contains("recordings folder is unavailable", exception.Message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchScreenRecorderTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
