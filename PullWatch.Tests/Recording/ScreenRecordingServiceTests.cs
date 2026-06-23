using ScreenRecorderLib;

namespace PullWatch.Tests;

public sealed class ScreenRecordingServiceTests
{
    [Fact]
    public void VideoEncoderOptionsUseCalculatedBitrate()
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

        Assert.Equal(24_000_000, options.Bitrate);
        Assert.Equal(VideoFrameRates.High, options.Framerate);
        Assert.True(options.IsFixedFramerate);
        Assert.Equal(H264BitrateControlMode.CBR, encoder.BitrateMode);
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
