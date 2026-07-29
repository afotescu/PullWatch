using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class FfmpegEncoderTestService(ILogger<FfmpegEncoderTestService> logger)
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(12);
    private static readonly VideoCaptureSize TestFrameSize = new(1920, 1080);

    public async Task<IReadOnlyList<VideoEncoderTestResult>> TestAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken
    )
    {
        return await TestAsync(settings, progress: null, cancellationToken);
    }

    public async Task<IReadOnlyList<VideoEncoderTestResult>> TestAsync(
        PullWatchSettings settings,
        IProgress<VideoEncoderTestProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        return await TestAsync(
            settings,
            FfmpegToolPaths.ResolveFfmpegPath(),
            progress,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<VideoEncoderTestResult>> TestAsync(
        PullWatchSettings settings,
        string ffmpegPath,
        IProgress<VideoEncoderTestProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);

        var testTimestamp = Stopwatch.GetTimestamp();
        var results = new List<VideoEncoderTestResult>();
        var profiles = GetTestProfiles();

        logger.LogInformation(
            "Starting FFmpeg video encoder test with {ProfileCount} profiles, synthetic source testsrc2 {Width}x{Height} at {FrameRate} FPS, FFmpeg path {FfmpegPath}",
            profiles.Count,
            TestFrameSize.Width,
            TestFrameSize.Height,
            settings.Video.FrameRate,
            ffmpegPath
        );

        try
        {
            for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = profiles[profileIndex];
                logger.LogInformation(
                    "Testing FFmpeg video encoder profile {CurrentProfile}/{TotalProfiles}: {VideoEncoder} ({VideoEncoderName})",
                    profileIndex + 1,
                    profiles.Count,
                    profile.DisplayName,
                    profile.EncoderName
                );
                progress?.Report(
                    new VideoEncoderTestProgress(
                        profileIndex,
                        profiles.Count,
                        ToProfileSelection(profile)
                    )
                );
                var result = await TestProviderAsync(
                    ffmpegPath,
                    settings,
                    profile,
                    cancellationToken
                );
                LogTestResult(profile, result);
                results.Add(result);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "FFmpeg video encoder test was canceled after {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(testTimestamp).TotalMilliseconds
            );
            throw;
        }

        if (profiles.Count > 0)
        {
            progress?.Report(
                new VideoEncoderTestProgress(
                    profiles.Count,
                    profiles.Count,
                    ToProfileSelection(profiles[profiles.Count - 1])
                )
            );
        }

        var passedCount = results.Count(result => result.IsAvailable);
        logger.LogInformation(
            "Finished FFmpeg video encoder test in {ElapsedMilliseconds:F1} ms: {PassedProfileCount}/{ProfileCount} profiles passed",
            Stopwatch.GetElapsedTime(testTimestamp).TotalMilliseconds,
            passedCount,
            profiles.Count
        );

        return results;
    }

    private static VideoProfileSelection ToProfileSelection(FfmpegVideoEncoderProfile profile)
    {
        return new VideoProfileSelection { Codec = profile.Codec, Provider = profile.Provider };
    }

    private void LogTestResult(FfmpegVideoEncoderProfile profile, VideoEncoderTestResult result)
    {
        if (result.IsAvailable)
        {
            logger.LogInformation(
                "FFmpeg video encoder profile passed: {VideoEncoder} ({VideoEncoderName}); {ResultMessage}",
                profile.DisplayName,
                result.EncoderName ?? profile.EncoderName,
                result.Message
            );
            return;
        }

        logger.LogInformation(
            "FFmpeg video encoder profile unavailable: {VideoEncoder} ({VideoEncoderName}); classification={FailureKind}; {ResultMessage}",
            profile.DisplayName,
            result.EncoderName ?? profile.EncoderName,
            result.FailureKind,
            result.Message
        );
    }

    private async Task<VideoEncoderTestResult> TestProviderAsync(
        string ffmpegPath,
        PullWatchSettings settings,
        FfmpegVideoEncoderProfile profile,
        CancellationToken cancellationToken
    )
    {
        var testSettings = settings with
        {
            Video = settings.Video with
            {
                SelectedProfile = new VideoProfileSelection
                {
                    Codec = profile.Codec,
                    Provider = profile.Provider,
                },
                CaptureCursor = false,
                ShowCaptureBorder = false,
            },
            Audio = settings.Audio with { CaptureSystemAudio = false, CaptureMicrophone = false },
        };
        var encoderCapabilities = new FfmpegEncoderCapabilities([profile.EncoderName]);

        FfmpegVideoEncoderOptions videoEncoderOptions;
        try
        {
            videoEncoderOptions = FfmpegEncoderOptionsFactory.CreateVideoEncoderOptions(
                testSettings,
                TestFrameSize,
                encoderCapabilities
            );
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return VideoEncoderTestResult.Unavailable(
                profile.Codec,
                profile.Provider,
                null,
                SimplifyMessage(exception.Message),
                VideoEncoderTestFailureKind.EncoderUnavailable
            );
        }

        var outputPath = CreateTestOutputPath(profile);
        var deleteOutput = true;
        try
        {
            var startInfo = CreateSyntheticTestStartInfo(
                ffmpegPath,
                testSettings,
                outputPath,
                videoEncoderOptions,
                TestDuration
            );
            var requestedFrameCount = GetRequestedFrameCount(testSettings.Video.FrameRate);
            logger.LogInformation(
                "FFmpeg calibration launch for {VideoEncoder} ({VideoEncoderName}): executable={FfmpegPath}; arguments(JSON)={FfmpegArguments}; input=lavfi testsrc2; dimensions={Width}x{Height}; frame rate={FrameRate}; requested frames={RequestedFrameCount}; audio=false; temporary output={OutputPath}; requested duration={RequestedDuration}; timeout={Timeout}",
                profile.DisplayName,
                profile.EncoderName,
                Path.GetFullPath(startInfo.FileName),
                JsonSerializer.Serialize(startInfo.ArgumentList.ToArray()),
                TestFrameSize.Width,
                TestFrameSize.Height,
                testSettings.Video.FrameRate,
                requestedFrameCount,
                outputPath,
                TestDuration,
                TestTimeout
            );
            var recordingResult = await FfmpegCalibrationProcessRunner.RunAsync(
                startInfo,
                outputPath,
                TestDuration,
                TestTimeout,
                cancellationToken
            );
            deleteOutput = recordingResult.ExitCode is not null;
            LogProcessDiagnostics(
                profile,
                startInfo,
                testSettings.Video.FrameRate,
                outputPath,
                recordingResult
            );

            if (recordingResult.CallerCancellationFired)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (recordingResult.TimeoutFired)
            {
                if (recordingResult.ExitCode is not null)
                {
                    await LogFailedProcessOutputAsync(
                        ffmpegPath,
                        outputPath,
                        profile,
                        TestFrameSize,
                        cancellationToken
                    );
                }

                var failureKind = ClassifyTimeout(recordingResult);
                return VideoEncoderTestResult.Unavailable(
                    profile.Codec,
                    profile.Provider,
                    videoEncoderOptions.EncoderName,
                    CreateTimeoutFailureMessage(failureKind, recordingResult),
                    failureKind
                );
            }

            if (recordingResult.ExitCode != 0)
            {
                await LogFailedProcessOutputAsync(
                    ffmpegPath,
                    outputPath,
                    profile,
                    TestFrameSize,
                    cancellationToken
                );
                var failureKind = ClassifyRecordingFailure(
                    recordingResult.StandardErrorTail,
                    recordingResult.StandardOutputTail
                );
                return VideoEncoderTestResult.Unavailable(
                    profile.Codec,
                    profile.Provider,
                    videoEncoderOptions.EncoderName,
                    CreateRecordingFailureMessage(
                        profile.Provider,
                        recordingResult.StandardErrorTail,
                        recordingResult.StandardOutputTail,
                        recordingResult.ExitCode ?? -1
                    ),
                    failureKind
                );
            }

            var validation = await ValidateOutputAsync(
                ffmpegPath,
                outputPath,
                profile.Codec,
                TestFrameSize,
                TestDuration,
                cancellationToken
            );
            if (!validation.IsValid)
            {
                LogOutputValidation(profile, outputPath, validation);
                return VideoEncoderTestResult.Unavailable(
                    profile.Codec,
                    profile.Provider,
                    videoEncoderOptions.EncoderName,
                    validation.Message,
                    validation.FileExists
                        ? VideoEncoderTestFailureKind.OutputFileInvalid
                        : VideoEncoderTestFailureKind.OutputFileMissing
                );
            }

            LogOutputValidation(profile, outputPath, validation);
            return VideoEncoderTestResult.Available(
                profile.Codec,
                profile.Provider,
                videoEncoderOptions.EncoderName,
                $"{validation.CodecName}, {validation.Width}x{validation.Height}, {FormatDuration(validation.Duration)}",
                validation.Width,
                validation.Height,
                validation.Duration
            );
        }
        catch (FfmpegEncoderTestValidationException exception)
            when (exception.InnerException is TimeoutException)
        {
            var file = GetFileSnapshot(outputPath);
            logger.LogWarning(
                "FFmpeg calibration output validation timed out for {VideoEncoder} ({VideoEncoderName}): path={OutputPath}; exists={FileExists}; size={FileSize} bytes; timeout={Timeout}",
                profile.DisplayName,
                profile.EncoderName,
                outputPath,
                file.Exists,
                file.Size,
                TestTimeout
            );
            return VideoEncoderTestResult.Unavailable(
                profile.Codec,
                profile.Provider,
                videoEncoderOptions.EncoderName,
                SimplifyMessage(exception.Message),
                VideoEncoderTestFailureKind.OutputValidationTimedOut
            );
        }
        catch (Exception exception)
            when (exception
                    is Win32Exception
                        or IOException
                        or InvalidOperationException
                        or TimeoutException
            )
        {
            var failureKind = exception is Win32Exception { NativeErrorCode: 2 }
                ? VideoEncoderTestFailureKind.EncoderUnavailable
                : VideoEncoderTestFailureKind.EncoderInitializationFailed;
            return VideoEncoderTestResult.Unavailable(
                profile.Codec,
                profile.Provider,
                videoEncoderOptions.EncoderName,
                SimplifyMessage(exception.Message),
                failureKind
            );
        }
        finally
        {
            if (deleteOutput)
            {
                TryDelete(outputPath);
            }
            else
            {
                logger.LogWarning(
                    "Preserving FFmpeg calibration output because the process did not exit: {OutputPath}",
                    outputPath
                );
            }
        }
    }

    internal static IReadOnlyList<FfmpegVideoEncoderProfile> GetTestProfiles()
    {
        return FfmpegEncoderOptionsFactory.GetCalibrationProfiles();
    }

    internal static ProcessStartInfo CreateSyntheticTestStartInfo(
        string ffmpegPath,
        PullWatchSettings settings,
        string outputPath,
        FfmpegVideoEncoderOptions videoEncoderOptions,
        TimeSpan testDuration
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(videoEncoderOptions);

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        var arguments = startInfo.ArgumentList;
        arguments.Add("-hide_banner");
        arguments.Add("-nostats");
        arguments.Add("-loglevel");
        arguments.Add("info");
        arguments.Add("-stats_period");
        arguments.Add("0.5");
        arguments.Add("-progress");
        arguments.Add("pipe:1");
        arguments.Add("-y");
        arguments.Add("-f");
        arguments.Add("lavfi");
        arguments.Add("-i");
        arguments.Add(
            $"testsrc2=size={TestFrameSize.Width}x{TestFrameSize.Height}:rate={settings.Video.FrameRate}"
        );
        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-an");

        foreach (var argument in videoEncoderOptions.CreateArguments())
        {
            arguments.Add(argument);
        }

        arguments.Add("-pix_fmt");
        arguments.Add(
            videoEncoderOptions.Provider == VideoEncoderProvider.Software ? "yuv420p" : "nv12"
        );
        arguments.Add("-frames:v");
        arguments.Add(
            GetRequestedFrameCount(settings.Video.FrameRate, testDuration)
                .ToString(CultureInfo.InvariantCulture)
        );
        arguments.Add(outputPath);
        return startInfo;
    }

    private static int GetRequestedFrameCount(int frameRate, TimeSpan testDuration = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        var duration = testDuration == default ? TestDuration : testDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        return checked((int)Math.Ceiling(frameRate * duration.TotalSeconds));
    }

    private static string CreateTestOutputPath(FfmpegVideoEncoderProfile profile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PullWatch", "EncoderTests");
        Directory.CreateDirectory(directory);
        return Path.Combine(
            directory,
            $"encoder-test-{profile.Codec}-{profile.Provider}-{Guid.NewGuid():N}.mp4"
        );
    }

    private async Task LogFailedProcessOutputAsync(
        string ffmpegPath,
        string outputPath,
        FfmpegVideoEncoderProfile profile,
        VideoCaptureSize outputSize,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var validation = await ValidateOutputAsync(
                ffmpegPath,
                outputPath,
                profile.Codec,
                outputSize,
                TestDuration,
                cancellationToken
            );
            LogOutputValidation(profile, outputPath, validation);
        }
        catch (FfmpegEncoderTestValidationException exception)
            when (exception.InnerException is TimeoutException)
        {
            var file = GetFileSnapshot(outputPath);
            logger.LogWarning(
                "FFmpeg calibration diagnostic output validation timed out for {VideoEncoder} ({VideoEncoderName}): path={OutputPath}; exists={FileExists}; size={FileSize} bytes; timeout={Timeout}",
                profile.DisplayName,
                profile.EncoderName,
                outputPath,
                file.Exists,
                file.Size,
                TestTimeout
            );
        }
        catch (FfmpegEncoderTestValidationException exception)
        {
            logger.LogWarning(
                exception,
                "FFmpeg calibration diagnostic output validation failed for {VideoEncoder} ({VideoEncoderName}): {OutputPath}",
                profile.DisplayName,
                profile.EncoderName,
                outputPath
            );
        }
    }

    private void LogProcessDiagnostics(
        FfmpegVideoEncoderProfile profile,
        ProcessStartInfo startInfo,
        int frameRate,
        string outputPath,
        FfmpegCalibrationProcessResult result
    )
    {
        var finalFile = GetFileSnapshot(outputPath);
        var diagnostics = new StringBuilder();
        diagnostics.AppendLine($"FFmpeg calibration process diagnostics: {profile.DisplayName}");
        diagnostics.AppendLine($"Encoder: {profile.EncoderName}");
        diagnostics.AppendLine($"Executable path: {Path.GetFullPath(startInfo.FileName)}");
        diagnostics.AppendLine(
            $"Arguments (JSON array): {JsonSerializer.Serialize(startInfo.ArgumentList.ToArray())}"
        );
        diagnostics.AppendLine($"Process id: {result.ProcessId}");
        diagnostics.AppendLine($"UTC start time: {result.StartTimeUtc:O}");
        diagnostics.AppendLine("Input source: lavfi testsrc2 (synthetic; no capture target)");
        diagnostics.AppendLine(
            $"Resolved dimensions: {TestFrameSize.Width}x{TestFrameSize.Height}; frame rate: {frameRate}; requested frames: {GetRequestedFrameCount(frameRate)}"
        );
        diagnostics.AppendLine("Audio capture enabled: false");
        diagnostics.AppendLine(
            $"Redirected streams: stdin={startInfo.RedirectStandardInput}; stdout={startInfo.RedirectStandardOutput}; stderr={startInfo.RedirectStandardError}"
        );
        diagnostics.AppendLine("FFmpeg stdout continuously captured: true");
        diagnostics.AppendLine("FFmpeg stderr continuously captured: true");
        diagnostics.AppendLine($"Latest FFmpeg progress: {result.LatestProgress ?? "(none)"}");
        diagnostics.AppendLine($"Latest encoded frame count: {result.LatestFrameCount}");
        diagnostics.AppendLine($"Any frames received: {result.AnyFramesReceived}");
        diagnostics.AppendLine($"Temporary output path: {outputPath}");
        diagnostics.AppendLine(
            $"Output observed during test: exists={result.OutputFileObserved}; maximum size={result.MaximumObservedOutputFileSize} bytes"
        );
        diagnostics.AppendLine($"Requested test duration: {TestDuration}; timeout: {TestTimeout}");
        diagnostics.AppendLine(
            $"Graceful shutdown attempted: {result.GracefulShutdownAttempted}; q sent: {result.QSent}; stdin closed: {result.StdinClosed}; exited after graceful shutdown: {result.ExitedAfterGracefulShutdown}; error: {result.GracefulShutdownError ?? "(none)"}"
        );
        diagnostics.AppendLine($"Timeout cancellation fired: {result.TimeoutFired}");
        diagnostics.AppendLine(
            $"Kill(entireProcessTree: true) called: {result.KillEntireProcessTreeCalled}; exited after kill: {result.ExitedAfterKill}; error: {result.KillError ?? "(none)"}"
        );
        diagnostics.AppendLine(
            $"Exit code: {result.ExitCode?.ToString() ?? "(unavailable)"}; elapsed: {result.Elapsed}"
        );
        diagnostics.AppendLine(
            $"Output after process exit: exists={finalFile.Exists}; size={finalFile.Size} bytes"
        );
        diagnostics.AppendLine("Last useful FFmpeg stderr (up to 100 lines):");
        diagnostics.Append(
            string.IsNullOrWhiteSpace(result.StandardErrorTail)
                ? "(none)"
                : result.StandardErrorTail
        );

        logger.LogInformation("{FfmpegCalibrationDiagnostics}", diagnostics.ToString());
    }

    private void LogOutputValidation(
        FfmpegVideoEncoderProfile profile,
        string outputPath,
        FfmpegTestOutputValidation validation
    )
    {
        logger.LogInformation(
            "FFmpeg calibration output validation for {VideoEncoder} ({VideoEncoderName}): valid={IsValid}; path={OutputPath}; exists={FileExists}; size={FileSize} bytes; duration={DurationSeconds}s; resolution={Width}x{Height}; codec={CodecName}; streams={StreamInformation}; message={ValidationMessage}",
            profile.DisplayName,
            profile.EncoderName,
            validation.IsValid,
            outputPath,
            validation.FileExists,
            validation.FileSize,
            validation.Duration,
            validation.Width,
            validation.Height,
            validation.CodecName ?? "(unknown)",
            validation.StreamInformation ?? "(unknown)",
            validation.Message
        );
    }

    internal static VideoEncoderTestFailureKind ClassifyTimeout(
        FfmpegCalibrationProcessResult result
    )
    {
        if (
            result.KillEntireProcessTreeCalled
            && !result.ExitedAfterKill
            && !result.ExitedAfterGracefulShutdown
        )
        {
            return VideoEncoderTestFailureKind.ProcessKillFailed;
        }

        if (result.GracefulShutdownAttempted && !result.QSent)
        {
            return VideoEncoderTestFailureKind.GracefulShutdownFailed;
        }

        if (!result.AnyFramesReceived)
        {
            return VideoEncoderTestFailureKind.NoFramesReceived;
        }

        return result.ReachedRequestedDuration
            ? VideoEncoderTestFailureKind.EncodedSuccessfullyButDidNotExit
            : VideoEncoderTestFailureKind.FfmpegTimedOutWhileActivelyEncoding;
    }

    internal static VideoEncoderTestFailureKind ClassifyRecordingFailure(
        string standardError,
        string standardOutput
    )
    {
        var diagnostic = $"{standardError}{Environment.NewLine}{standardOutput}";
        if (
            ContainsAny(
                diagnostic,
                "unknown encoder",
                "encoder not found",
                "requested encoder is not available"
            )
        )
        {
            return VideoEncoderTestFailureKind.EncoderUnavailable;
        }

        if (
            diagnostic.Contains("gfxcapture", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(diagnostic, "could not", "error", "failed", "invalid", "not supported")
        )
        {
            return VideoEncoderTestFailureKind.CaptureInitializationFailed;
        }

        return VideoEncoderTestFailureKind.EncoderInitializationFailed;
    }

    private static string CreateTimeoutFailureMessage(
        VideoEncoderTestFailureKind failureKind,
        FfmpegCalibrationProcessResult result
    )
    {
        var detail = failureKind switch
        {
            VideoEncoderTestFailureKind.NoFramesReceived =>
                "no frames received; the synthetic test source did not deliver a frame before the test timeout",
            VideoEncoderTestFailureKind.FfmpegTimedOutWhileActivelyEncoding =>
                $"FFmpeg timed out while actively encoding; latest frame {result.LatestFrameCount}, output time {result.LatestOutTime}",
            VideoEncoderTestFailureKind.EncodedSuccessfullyButDidNotExit =>
                $"FFmpeg reached the requested duration but did not exit; latest frame {result.LatestFrameCount}, output time {result.LatestOutTime}",
            VideoEncoderTestFailureKind.GracefulShutdownFailed =>
                $"graceful shutdown failed; q could not be sent: {result.GracefulShutdownError ?? "no error was reported"}",
            VideoEncoderTestFailureKind.ProcessKillFailed =>
                $"process kill failed; FFmpeg did not exit after Kill(entireProcessTree: true): {result.KillError ?? "no error was reported"}",
            _ => $"FFmpeg did not finish within {TestTimeout}",
        };

        return $"{detail}.";
    }

    private static (bool Exists, long Size) GetFileSnapshot(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (true, file.Length) : (false, 0);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, 0);
        }
    }

    internal static async Task<FfmpegTestOutputValidation> ValidateOutputAsync(
        string ffmpegPath,
        string outputPath,
        VideoCodec codec,
        VideoCaptureSize outputSize,
        TimeSpan expectedDuration,
        CancellationToken cancellationToken
    )
    {
        var file = GetFileSnapshot(outputPath);
        if (!file.Exists || file.Size == 0)
        {
            return FfmpegTestOutputValidation.Invalid(
                "No output file was produced.",
                file.Exists,
                file.Size
            );
        }

        var startInfo = new ProcessStartInfo(ffmpegPath);
        foreach (
            var argument in new[]
            {
                "-hide_banner",
                "-v",
                "info",
                "-xerror",
                "-i",
                outputPath,
                "-map",
                "0:v:0",
                "-frames:v",
                "1",
                "-f",
                "null",
                "-",
            }
        )
        {
            startInfo.ArgumentList.Add(argument);
        }

        ExternalProcessResult result;
        try
        {
            result = await ExternalProcessRunner.RunAsync(
                startInfo,
                TestTimeout,
                cancellationToken,
                $"{Path.GetFileName(startInfo.FileName)} test"
            );
        }
        catch (Exception exception)
            when (exception
                    is Win32Exception
                        or IOException
                        or InvalidOperationException
                        or TimeoutException
            )
        {
            throw new FfmpegEncoderTestValidationException(
                $"ffmpeg validation could not run: {SimplifyMessage(exception.Message)}",
                exception
            );
        }

        if (result.ExitCode != 0)
        {
            return FfmpegTestOutputValidation.Invalid(
                CreateFailureMessage("ffmpeg validation failed", result),
                file.Exists,
                file.Size,
                GetStreamInformation(result.StandardError)
            );
        }

        var metadata = ParseOutputMetadata(result.StandardError);
        if (
            metadata.CodecName is null
            || metadata.Width <= 0
            || metadata.Height <= 0
            || metadata.Duration <= 0
        )
        {
            return FfmpegTestOutputValidation.Invalid(
                "ffmpeg validation could decode the output but could not read complete video metadata.",
                file.Exists,
                file.Size,
                metadata.StreamInformation
            );
        }

        var expectedCodec = FormatCodecName(codec);
        if (
            !metadata.CodecName.Equals(expectedCodec, StringComparison.OrdinalIgnoreCase)
            || metadata.Width != outputSize.Width
            || metadata.Height != outputSize.Height
            || metadata.Duration < expectedDuration.TotalSeconds - 0.25
        )
        {
            return FfmpegTestOutputValidation.Invalid(
                $"ffmpeg validation found {metadata.CodecName}, {metadata.Width}x{metadata.Height}, {metadata.Duration:0.###}s; expected {expectedCodec}, {outputSize.Width}x{outputSize.Height}, approximately {expectedDuration.TotalSeconds:0.###}s.",
                file.Exists,
                file.Size,
                metadata.StreamInformation,
                metadata.CodecName,
                metadata.Width,
                metadata.Height,
                metadata.Duration
            );
        }

        return FfmpegTestOutputValidation.Valid(
            metadata.CodecName,
            metadata.Width,
            metadata.Height,
            metadata.Duration,
            file.Size,
            metadata.StreamInformation
        );
    }

    internal static FfmpegOutputMetadata ParseOutputMetadata(string standardError)
    {
        var durationMatch = Regex.Match(
            standardError,
            @"Duration:\s*(?<hours>\d{2}):(?<minutes>\d{2}):(?<seconds>\d{2}(?:\.\d+)?)",
            RegexOptions.CultureInvariant
        );
        var videoLine = ReadMeaningfulLines(standardError)
            .FirstOrDefault(line => line.Contains("Video:", StringComparison.OrdinalIgnoreCase));
        if (!durationMatch.Success || videoLine is null)
        {
            return new FfmpegOutputMetadata(null, 0, 0, 0, GetStreamInformation(standardError));
        }

        var videoMatch = Regex.Match(
            videoLine,
            @"Video:\s*(?<codec>[^,\s(]+).*?(?<width>\d{2,5})x(?<height>\d{2,5})(?:[,\s])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
        if (!videoMatch.Success)
        {
            return new FfmpegOutputMetadata(null, 0, 0, 0, GetStreamInformation(standardError));
        }

        var duration =
            TimeSpan.FromHours(int.Parse(durationMatch.Groups["hours"].Value))
            + TimeSpan.FromMinutes(int.Parse(durationMatch.Groups["minutes"].Value))
            + TimeSpan.FromSeconds(
                double.Parse(
                    durationMatch.Groups["seconds"].Value,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );

        return new FfmpegOutputMetadata(
            videoMatch.Groups["codec"].Value,
            int.Parse(videoMatch.Groups["width"].Value),
            int.Parse(videoMatch.Groups["height"].Value),
            duration.TotalSeconds,
            GetStreamInformation(standardError)
        );
    }

    private static string? GetStreamInformation(string standardError)
    {
        var streams = ReadMeaningfulLines(standardError)
            .Where(line => line.StartsWith("Stream #", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return streams.Length == 0 ? null : string.Join(" | ", streams);
    }

    private static string CreateFailureMessage(string prefix, ExternalProcessResult result)
    {
        return CreateFailureMessage(
            prefix,
            result.StandardError,
            result.StandardOutput,
            result.ExitCode
        );
    }

    internal static string CreateRecordingFailureMessage(
        VideoEncoderProvider provider,
        string standardError,
        string standardOutput,
        int exitCode
    )
    {
        var detail = SelectFailureDetail(standardError, standardOutput);
        if (IsHardwareProbeRejection(provider, detail))
        {
            return "recording failed; encoder is present in FFmpeg, but the current hardware or driver stack rejected the test encode.";
        }

        return CreateFailureMessage("recording failed", standardError, standardOutput, exitCode);
    }

    private static string CreateFailureMessage(
        string prefix,
        string standardError,
        string standardOutput,
        int exitCode
    )
    {
        var detail = SelectFailureDetail(standardError, standardOutput);
        return detail is null
            ? $"{prefix}; exit code {exitCode}."
            : $"{prefix}; {SimplifyMessage(detail)}";
    }

    private static bool IsHardwareProbeRejection(VideoEncoderProvider provider, string? detail)
    {
        return provider != VideoEncoderProvider.Software
            && detail is not null
            && ContainsAny(
                detail,
                "invalid argument",
                "no capable devices",
                "no device",
                "device not found",
                "not available",
                "not supported",
                "unsupported",
                "cannot load",
                "failed to create",
                "failed to initialise",
                "failed to initialize"
            );
    }

    internal static string? SelectFailureDetail(string standardError, string standardOutput)
    {
        return SelectDiagnosticLine(standardError) ?? SelectDiagnosticLine(standardOutput);
    }

    private static string? SelectDiagnosticLine(string text)
    {
        var lines = ReadMeaningfulLines(text);
        return lines.LastOrDefault(IsSpecificDiagnosticLine)
            ?? lines.LastOrDefault(IsDiagnosticLine)
            ?? lines.LastOrDefault(line => !IsFfmpegNoiseLine(line))
            ?? lines.FirstOrDefault();
    }

    private static string[] ReadMeaningfulLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line.Trim());
            }
        }

        return lines.ToArray();
    }

    private static bool IsSpecificDiagnosticLine(string line)
    {
        return ContainsAny(
                line,
                "not divisible",
                "error initializing",
                "error while",
                "could not",
                "impossible",
                "invalid",
                "no capable",
                "not supported",
                "unsupported",
                "function not implemented",
                "failed"
            ) && !line.Equals("Conversion failed!", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiagnosticLine(string line)
    {
        return IsSpecificDiagnosticLine(line)
            || ContainsAny(line, "error", "failed")
            || line.Equals("Conversion failed!", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFfmpegNoiseLine(string line)
    {
        return line.Equals("Stream mapping:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Stream #", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Input #", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Output #", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Duration:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Metadata:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Press [q]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string line, params string[] values)
    {
        return values.Any(value => line.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string SimplifyMessage(string message)
    {
        return string.IsNullOrWhiteSpace(message) ? "No details were reported." : message.Trim();
    }

    private static string FormatDuration(double duration)
    {
        return duration <= 0 ? "duration unknown" : $"{duration:0.0}s";
    }

    private static string FormatCodecName(VideoCodec codec)
    {
        return codec switch
        {
            VideoCodec.H264 => "h264",
            VideoCodec.H265 => "hevc",
            _ => VideoProfileFormatter.FormatCodecName(codec),
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Test files are temporary diagnostics; cleanup failure should not hide the result.
        }
    }
}

internal sealed class FfmpegEncoderTestValidationException(string message, Exception innerException)
    : Exception(message, innerException);

internal sealed record FfmpegOutputMetadata(
    string? CodecName,
    int Width,
    int Height,
    double Duration,
    string? StreamInformation
);

internal sealed record FfmpegTestOutputValidation(
    bool IsValid,
    string Message,
    string? CodecName,
    int Width,
    int Height,
    double Duration,
    bool FileExists,
    long FileSize,
    string? StreamInformation
)
{
    public static FfmpegTestOutputValidation Valid(
        string codecName,
        int width,
        int height,
        double duration,
        long fileSize,
        string? streamInformation
    )
    {
        return new FfmpegTestOutputValidation(
            true,
            string.Empty,
            codecName,
            width,
            height,
            duration,
            true,
            fileSize,
            streamInformation
        );
    }

    public static FfmpegTestOutputValidation Invalid(
        string message,
        bool fileExists,
        long fileSize,
        string? streamInformation = null,
        string? codecName = null,
        int width = 0,
        int height = 0,
        double duration = 0
    )
    {
        return new FfmpegTestOutputValidation(
            false,
            message,
            codecName,
            width,
            height,
            duration,
            fileExists,
            fileSize,
            streamInformation
        );
    }
}

public enum VideoEncoderTestFailureKind
{
    None,
    EncoderUnavailable,
    EncoderInitializationFailed,
    CaptureInitializationFailed,
    NoFramesReceived,
    FfmpegTimedOutWhileActivelyEncoding,
    EncodedSuccessfullyButDidNotExit,
    GracefulShutdownFailed,
    ProcessKillFailed,
    OutputFileMissing,
    OutputFileInvalid,
    OutputValidationTimedOut,
}

public sealed record VideoEncoderTestResult(
    VideoCodec Codec,
    VideoEncoderProvider Provider,
    string? EncoderName,
    bool IsAvailable,
    string Message,
    int Width,
    int Height,
    double DurationSeconds,
    VideoEncoderTestFailureKind FailureKind = VideoEncoderTestFailureKind.None
)
{
    public static VideoEncoderTestResult Available(
        VideoCodec codec,
        VideoEncoderProvider provider,
        string encoderName,
        string message,
        int width,
        int height,
        double durationSeconds
    )
    {
        return new VideoEncoderTestResult(
            codec,
            provider,
            encoderName,
            true,
            message,
            width,
            height,
            durationSeconds,
            VideoEncoderTestFailureKind.None
        );
    }

    public static VideoEncoderTestResult Unavailable(
        VideoCodec codec,
        VideoEncoderProvider provider,
        string? encoderName,
        string message,
        VideoEncoderTestFailureKind failureKind =
            VideoEncoderTestFailureKind.EncoderInitializationFailed
    )
    {
        return new VideoEncoderTestResult(
            codec,
            provider,
            encoderName,
            false,
            message,
            0,
            0,
            0,
            failureKind
        );
    }

    public EncoderCalibrationResult ToCalibrationResult()
    {
        return new EncoderCalibrationResult
        {
            Codec = Codec,
            Provider = Provider,
            EncoderName = EncoderName ?? string.Empty,
            Passed = IsAvailable,
            Message = Message,
            Width = Width,
            Height = Height,
            DurationSeconds = DurationSeconds,
            FailureKind = FailureKind,
        };
    }
}

public sealed record VideoEncoderTestProgress(
    int CompletedProfiles,
    int TotalProfiles,
    VideoProfileSelection CurrentProfile
);
