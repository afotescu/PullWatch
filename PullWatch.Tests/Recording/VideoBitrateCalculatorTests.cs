namespace PullWatch.Tests;

public sealed class VideoBitrateCalculatorTests
{
    [Theory]
    [InlineData(1920, 1080, 60, VideoQuality.Compact, 6)]
    [InlineData(1920, 1080, 60, VideoQuality.Balanced, 9)]
    [InlineData(1920, 1080, 60, VideoQuality.High, 12)]
    [InlineData(2560, 1440, 60, VideoQuality.Compact, 10)]
    [InlineData(2560, 1440, 60, VideoQuality.Balanced, 16)]
    [InlineData(2560, 1440, 60, VideoQuality.High, 22)]
    [InlineData(3840, 2160, 60, VideoQuality.Compact, 24)]
    [InlineData(3840, 2160, 60, VideoQuality.Balanced, 35)]
    [InlineData(3840, 2160, 60, VideoQuality.High, 50)]
    [InlineData(2560, 1440, 30, VideoQuality.Balanced, 8)]
    [InlineData(3440, 1440, 60, VideoQuality.Balanced, 20)]
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
    public void CalculatesLowerTargetBitrateForH265()
    {
        Assert.Equal(
            5_000_000,
            VideoBitrateCalculator.CalculateBitrate(
                new VideoCaptureSize(1920, 1080),
                VideoFrameRates.High,
                VideoQuality.Balanced,
                VideoCodec.H265
            )
        );
        Assert.Equal(
            14_000_000,
            VideoBitrateCalculator.CalculateBitrate(
                new VideoCaptureSize(2560, 1440),
                VideoFrameRates.High,
                VideoQuality.High,
                VideoCodec.H265
            )
        );
    }

    [Fact]
    public void EstimatesFiveMinuteFileSizeWithRecorderAudioDefaults()
    {
        var megabytes = VideoBitrateCalculator.EstimateFileSizeMegabytes(
            24_000_000,
            new AudioSettings { CaptureSystemAudio = true },
            TimeSpan.FromMinutes(5)
        );

        Assert.Equal(904, megabytes);
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
