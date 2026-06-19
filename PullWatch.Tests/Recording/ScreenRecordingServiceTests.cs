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
}
