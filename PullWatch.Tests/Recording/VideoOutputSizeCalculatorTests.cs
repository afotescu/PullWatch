namespace PullWatch.Tests;

public sealed class VideoOutputSizeCalculatorTests
{
    [Theory]
    [InlineData(3840, 2160, VideoScaling.Target1440p, 2560, 1440)]
    [InlineData(3840, 2160, VideoScaling.Optimized, 1920, 1080)]
    [InlineData(3840, 2160, VideoScaling.Target720p, 1280, 720)]
    [InlineData(2560, 1440, VideoScaling.Optimized, 1920, 1080)]
    [InlineData(3440, 1440, VideoScaling.Optimized, 2580, 1080)]
    [InlineData(1600, 1200, VideoScaling.Optimized, 1440, 1080)]
    [InlineData(1600, 1200, VideoScaling.Target720p, 960, 720)]
    [InlineData(1080, 1920, VideoScaling.Target720p, 720, 1280)]
    [InlineData(1920, 1080, VideoScaling.Target1440p, 1920, 1080)]
    [InlineData(1600, 900, VideoScaling.Target1440p, 1600, 900)]
    [InlineData(2560, 1440, VideoScaling.Original, 2560, 1440)]
    public void CalculatesOutputSize(
        int captureWidth,
        int captureHeight,
        VideoScaling scaling,
        int expectedWidth,
        int expectedHeight
    )
    {
        var outputSize = VideoOutputSizeCalculator.CalculateOutputSize(
            new VideoCaptureSize(captureWidth, captureHeight),
            scaling
        );

        Assert.Equal(new VideoCaptureSize(expectedWidth, expectedHeight), outputSize);
    }

    [Fact]
    public void RejectsNonPositiveCaptureSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoOutputSizeCalculator.CalculateOutputSize(
                new VideoCaptureSize(0, 1080),
                VideoScaling.Optimized
            )
        );
    }
}
