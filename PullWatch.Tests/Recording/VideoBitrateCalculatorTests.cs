namespace PullWatch.Tests;

public sealed class VideoBitrateCalculatorTests
{
    [Theory]
    [InlineData(1920, 1080, 60, VideoQuality.Compact, 4)]
    [InlineData(1920, 1080, 60, VideoQuality.Balanced, 8)]
    [InlineData(1920, 1080, 60, VideoQuality.High, 14)]
    [InlineData(2560, 1440, 60, VideoQuality.Compact, 8)]
    [InlineData(2560, 1440, 60, VideoQuality.Balanced, 14)]
    [InlineData(2560, 1440, 60, VideoQuality.High, 24)]
    [InlineData(3840, 2160, 60, VideoQuality.Compact, 18)]
    [InlineData(3840, 2160, 60, VideoQuality.Balanced, 30)]
    [InlineData(3840, 2160, 60, VideoQuality.High, 55)]
    [InlineData(2560, 1440, 30, VideoQuality.Balanced, 7)]
    [InlineData(3440, 1440, 60, VideoQuality.Balanced, 18)]
    public void CalculatesFriendlyBitrateFromCaptureSize(
        int width,
        int height,
        int frameRate,
        VideoQuality quality,
        int expectedMegabits
    )
    {
        var bitrate = VideoBitrateCalculator.CalculateBitrate(
            new VideoCaptureSize(width, height),
            frameRate,
            quality
        );

        Assert.Equal(expectedMegabits * 1_000_000, bitrate);
    }

    [Fact]
    public void EstimatesFiveMinuteFileSizeWithAudio()
    {
        var megabytes = VideoBitrateCalculator.EstimateFileSizeMegabytes(
            24_000_000,
            new AudioSettings { CaptureSystemAudio = true },
            TimeSpan.FromMinutes(5)
        );

        Assert.Equal(907, megabytes);
    }

    [Fact]
    public void ClampsExtremeBitrates()
    {
        var low = VideoBitrateCalculator.CalculateBitrate(
            new VideoCaptureSize(320, 180),
            30,
            VideoQuality.Compact
        );
        var high = VideoBitrateCalculator.CalculateBitrate(
            new VideoCaptureSize(7680, 4320),
            60,
            VideoQuality.High
        );

        Assert.Equal(VideoBitrateCalculator.MinimumBitrate, low);
        Assert.Equal(VideoBitrateCalculator.MaximumBitrate, high);
    }
}
