using System.Diagnostics;

namespace PullWatch.Tests;

public sealed class FfmpegCalibrationProcessRunnerTests
{
    [Fact]
    public async Task TimeoutSendsQClosesStdinKillsTreeAndWaitsForExit()
    {
        var startInfo = CreateCommand("ping 127.0.0.1 -n 30 > nul");
        startInfo.RedirectStandardInput = true;
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"PullWatch-calibration-runner-{Guid.NewGuid():N}.mp4"
        );

        var result = await FfmpegCalibrationProcessRunner.RunAsync(
            startInfo,
            outputPath,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.TimeoutFired);
        Assert.True(result.GracefulShutdownAttempted);
        Assert.True(result.QSent);
        Assert.True(result.StdinClosed);
        Assert.True(result.KillEntireProcessTreeCalled);
        Assert.True(result.ExitedAfterKill);
        Assert.Null(result.KillError);
        Assert.Equal(
            VideoEncoderTestFailureKind.NoFramesReceived,
            FfmpegEncoderTestService.ClassifyTimeout(result)
        );
    }

    [Fact]
    public async Task ContinuouslyCapturesLatestFfmpegProgressWithoutRequiringProcessExit()
    {
        var startInfo = CreateCommand(
            "echo frame=12 & echo fps=60.0 & echo out_time=00:00:00.200000 & echo progress=continue & ping 127.0.0.1 -n 30 > nul"
        );
        startInfo.RedirectStandardInput = true;
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"PullWatch-calibration-runner-{Guid.NewGuid():N}.mp4"
        );

        var result = await FfmpegCalibrationProcessRunner.RunAsync(
            startInfo,
            outputPath,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.TimeoutFired);
        Assert.True(result.AnyFramesReceived);
        Assert.Equal(12, result.LatestFrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.LatestOutTime);
        Assert.Contains("frame=12", result.LatestProgress);
        Assert.Contains("progress=continue", result.LatestProgress);
        Assert.Equal(
            VideoEncoderTestFailureKind.FfmpegTimedOutWhileActivelyEncoding,
            FfmpegEncoderTestService.ClassifyTimeout(result)
        );
        Assert.Equal(
            VideoEncoderTestFailureKind.EncodedSuccessfullyButDidNotExit,
            FfmpegEncoderTestService.ClassifyTimeout(
                result with
                {
                    LatestOutTime = TimeSpan.FromSeconds(2),
                }
            )
        );
    }

    [Theory]
    [InlineData("Unknown encoder 'hevc_amf'", VideoEncoderTestFailureKind.EncoderUnavailable)]
    [InlineData(
        "[gfxcapture @ 1] Failed to create capture item",
        VideoEncoderTestFailureKind.CaptureInitializationFailed
    )]
    [InlineData(
        "[hevc_nvenc @ 1] No capable devices found",
        VideoEncoderTestFailureKind.EncoderInitializationFailed
    )]
    public void RecordingFailuresHaveDistinctClassifications(
        string standardError,
        VideoEncoderTestFailureKind expected
    )
    {
        Assert.Equal(
            expected,
            FfmpegEncoderTestService.ClassifyRecordingFailure(standardError, string.Empty)
        );
    }

    private static ProcessStartInfo CreateCommand(string command)
    {
        var commandPath =
            Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo(commandPath);
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }
}
