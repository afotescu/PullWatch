using ScreenRecorderLib;

namespace PullWatch.Tests;

public sealed class ScreenRecordingServiceTests
{
    [Fact]
    public void VideoEncoderOptionsUseCalculatedBitrateAndLongRecordingDefaults()
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
            new VideoCaptureSize(2560, 1440)
        );
        var encoder = Assert.IsType<H264VideoEncoder>(options.Encoder);

        Assert.Equal(14_000_000, options.Bitrate);
        Assert.Equal(VideoFrameRates.High, options.Framerate);
        Assert.True(options.IsHardwareEncodingEnabled);
        Assert.False(options.IsLowLatencyEnabled);
        Assert.False(options.IsFixedFramerate);
        Assert.False(options.IsThrottlingDisabled);
        Assert.True(options.IsFragmentedMp4Enabled);
        Assert.False(options.IsMp4FastStartEnabled);
        Assert.Equal(H264BitrateControlMode.UnconstrainedVBR, encoder.BitrateMode);
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
