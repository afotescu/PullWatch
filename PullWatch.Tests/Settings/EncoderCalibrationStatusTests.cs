namespace PullWatch.Tests;

public sealed class EncoderCalibrationStatusTests
{
    private static readonly EncoderCalibrationEnvironment CurrentEnvironment = new(
        @"C:\ffmpeg\bin\ffmpeg.exe",
        "ffmpeg version test",
        @"C:\ffmpeg\bin\ffprobe.exe",
        "ffprobe version test"
    );

    [Fact]
    public void MissingWhenNoCalibrationResultsExist()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            new PullWatchSettings(),
            CurrentEnvironment
        );

        Assert.Equal(EncoderCalibrationStatusKind.Missing, status.Kind);
        Assert.False(status.IsValid);
        Assert.Equal("Video encoding needs to be tested before recording.", status.Message);
    }

    [Fact]
    public void MissingWhenNoSelectedProfileExists()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(includeSelectedProfile: false),
            CurrentEnvironment
        );

        Assert.Equal(EncoderCalibrationStatusKind.Missing, status.Kind);
    }

    [Fact]
    public void StaleWhenCalibrationVersionChanges()
    {
        var settings = CreateSettings(
            calibrationVersion: EncoderCalibrationSettings.CurrentVersion + 1
        );

        var status = EncoderCalibrationStatusEvaluator.Evaluate(settings, CurrentEnvironment);

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("app update", status.Message);
    }

    [Fact]
    public void StaleWhenFfmpegPathChanges()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(),
            CurrentEnvironment with
            {
                FfmpegPath = @"D:\Tools\ffmpeg.exe",
            }
        );

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("FFmpeg path", status.Message);
    }

    [Fact]
    public void StaleWhenFfmpegVersionChanges()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(),
            CurrentEnvironment with
            {
                FfmpegVersion = "ffmpeg version changed",
            }
        );

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("FFmpeg version", status.Message);
    }

    [Fact]
    public void StaleWhenFfprobePathChanges()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(),
            CurrentEnvironment with
            {
                FfprobePath = @"D:\Tools\ffprobe.exe",
            }
        );

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("FFprobe path", status.Message);
    }

    [Fact]
    public void StaleWhenFfprobeVersionChanges()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(),
            CurrentEnvironment with
            {
                FfprobeVersion = "ffprobe version changed",
            }
        );

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("FFprobe version", status.Message);
    }

    [Fact]
    public void StaleWhenSelectedProfileHasNoResult()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(
                selectedProfile: Profile(VideoCodec.H264, VideoEncoderProvider.AmdAmf),
                result: Result(VideoCodec.H265, VideoEncoderProvider.NvidiaNvenc, passed: true)
            ),
            CurrentEnvironment
        );

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("has not been tested", status.Message);
    }

    [Fact]
    public void StaleWhenSelectedProfileFailed()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(
                result: Result(VideoCodec.H265, VideoEncoderProvider.NvidiaNvenc, passed: false)
            ),
            CurrentEnvironment
        );

        Assert.Equal(EncoderCalibrationStatusKind.Stale, status.Kind);
        Assert.Contains("did not pass", status.Message);
    }

    [Fact]
    public void ValidWhenSelectedProfilePassedAndMetadataMatches()
    {
        var status = EncoderCalibrationStatusEvaluator.Evaluate(
            CreateSettings(),
            CurrentEnvironment
        );

        Assert.True(status.IsValid);
        Assert.Equal(EncoderCalibrationStatusKind.Valid, status.Kind);
        Assert.Equal("Video encoding is ready.", status.Message);
    }

    private static PullWatchSettings CreateSettings(
        VideoProfileSelection? selectedProfile = null,
        EncoderCalibrationResult? result = null,
        int calibrationVersion = EncoderCalibrationSettings.CurrentVersion,
        bool includeSelectedProfile = true
    )
    {
        selectedProfile = includeSelectedProfile
            ? selectedProfile ?? Profile(VideoCodec.H265, VideoEncoderProvider.NvidiaNvenc)
            : null;
        result ??= Result(
            selectedProfile?.Codec ?? VideoCodec.H265,
            selectedProfile?.Provider ?? VideoEncoderProvider.NvidiaNvenc,
            passed: true
        );

        return new PullWatchSettings
        {
            Video = new VideoSettings { SelectedProfile = selectedProfile },
            EncoderCalibration = new EncoderCalibrationSettings
            {
                Version = calibrationVersion,
                TestedAt = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
                FfmpegPath = CurrentEnvironment.FfmpegPath,
                FfmpegVersion = CurrentEnvironment.FfmpegVersion,
                FfprobePath = CurrentEnvironment.FfprobePath,
                FfprobeVersion = CurrentEnvironment.FfprobeVersion,
                Results = [result],
            },
        };
    }

    private static VideoProfileSelection Profile(VideoCodec codec, VideoEncoderProvider provider)
    {
        return new VideoProfileSelection { Codec = codec, Provider = provider };
    }

    private static EncoderCalibrationResult Result(
        VideoCodec codec,
        VideoEncoderProvider provider,
        bool passed
    )
    {
        return new EncoderCalibrationResult
        {
            Codec = codec,
            Provider = provider,
            EncoderName = codec == VideoCodec.H265 ? "hevc_nvenc" : "h264_amf",
            Passed = passed,
            Message = passed ? "Available" : "Unavailable",
            Width = passed ? 1920 : 0,
            Height = passed ? 1080 : 0,
            DurationSeconds = passed ? 2.0 : 0,
        };
    }
}
