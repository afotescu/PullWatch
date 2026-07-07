namespace PullWatch;

public readonly record struct VideoCaptureSize(int Width, int Height);

public static class VideoBitrateCalculator
{
    public const int MinimumBitrate = 4_000_000;
    public const int MaximumBitrate = 100_000_000;

    private const int BitsPerMegabit = 1_000_000;

    public static int CalculateBitrate(
        VideoCaptureSize captureSize,
        int frameRate,
        VideoQuality quality,
        VideoCodec codec = VideoCodec.H264
    )
    {
        if (captureSize.Width <= 0 || captureSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureSize),
                "Capture dimensions must be positive."
            );
        }

        var rawMegabits =
            captureSize.Width
            * (double)captureSize.Height
            * frameRate
            * GetQualityFactor(quality)
            * GetCodecFactor(codec)
            / BitsPerMegabit;
        var clampedMegabits = Math.Clamp(
            rawMegabits,
            MinimumBitrate / (double)BitsPerMegabit,
            MaximumBitrate / (double)BitsPerMegabit
        );

        return RoundToFriendlyMegabits(clampedMegabits) * BitsPerMegabit;
    }

    public static int EstimateFileSizeMegabytes(
        int videoBitrate,
        AudioSettings audio,
        TimeSpan duration
    )
    {
        ArgumentNullException.ThrowIfNull(audio);

        ArgumentOutOfRangeException.ThrowIfNegative(videoBitrate);

        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        var audioBitrate =
            audio.CaptureSystemAudio || audio.CaptureMicrophone
                ? RecordingAudioDefaults.BitrateBitsPerSecond
                : 0;
        var bytes = (videoBitrate + audioBitrate) * duration.TotalSeconds / 8;

        return (int)Math.Round(bytes / 1_000_000d, MidpointRounding.AwayFromZero);
    }

    public static int ToMegabitsPerSecond(int bitrate)
    {
        return (int)Math.Round(bitrate / (double)BitsPerMegabit, MidpointRounding.AwayFromZero);
    }

    private static double GetQualityFactor(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Compact => 0.07,
            VideoQuality.Balanced => 0.10,
            VideoQuality.High => 0.14,
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
        };
    }

    private static double GetCodecFactor(VideoCodec codec)
    {
        return codec switch
        {
            VideoCodec.H264 => 1,
            VideoCodec.H265 => 0.6,
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };
    }

    private static int RoundToFriendlyMegabits(double megabits)
    {
        return megabits switch
        {
            < 10 => (int)Math.Round(megabits, MidpointRounding.AwayFromZero),
            < 30 => (int)(Math.Round(megabits / 2d, MidpointRounding.AwayFromZero) * 2),
            _ => (int)(Math.Round(megabits / 5d, MidpointRounding.AwayFromZero) * 5),
        };
    }
}
