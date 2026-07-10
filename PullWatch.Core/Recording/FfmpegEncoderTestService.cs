using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class FfmpegEncoderTestService(
    Func<nint> getWindowHandle,
    ILogger<FfmpegEncoderTestService> logger
)
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(12);

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
        ArgumentNullException.ThrowIfNull(settings);

        var testTimestamp = Stopwatch.GetTimestamp();
        var windowHandle = getWindowHandle();
        if (windowHandle == nint.Zero)
        {
            logger.LogWarning(
                "FFmpeg video encoder test could not start because no capture window was available"
            );
            throw new InvalidOperationException("Could not find the PullWatch window to capture.");
        }

        var captureSize = WowWindowCaptureSizeDetector.GetCaptureSize(windowHandle);
        var outputSize = FfmpegVideoOutputSizeCalculator.CalculateOutputSize(
            captureSize,
            settings.Video.Scaling
        );
        var ffmpegPath = FfmpegToolPaths.ResolveFfmpegPath();
        var results = new List<VideoEncoderTestResult>();
        var profiles = GetTestProfiles();

        logger.LogInformation(
            "Starting FFmpeg video encoder test with {ProfileCount} profiles, capture {CaptureWidth}x{CaptureHeight}, output {OutputWidth}x{OutputHeight}, FFmpeg path {FfmpegPath}",
            profiles.Count,
            captureSize.Width,
            captureSize.Height,
            outputSize.Width,
            outputSize.Height,
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
                    windowHandle,
                    settings,
                    profile,
                    captureSize,
                    outputSize,
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
            "FFmpeg video encoder profile unavailable: {VideoEncoder} ({VideoEncoderName}); {ResultMessage}",
            profile.DisplayName,
            result.EncoderName ?? profile.EncoderName,
            result.Message
        );
    }

    private static async Task<VideoEncoderTestResult> TestProviderAsync(
        string ffmpegPath,
        nint windowHandle,
        PullWatchSettings settings,
        FfmpegVideoEncoderProfile profile,
        VideoCaptureSize captureSize,
        VideoCaptureSize outputSize,
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
                outputSize,
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
                SimplifyMessage(exception.Message)
            );
        }

        var outputPath = CreateTestOutputPath(profile);
        try
        {
            var startInfo = FfmpegRecordingService.CreateStartInfo(
                ffmpegPath,
                windowHandle,
                testSettings,
                captureSize,
                outputSize,
                outputPath,
                null,
                videoEncoderOptions,
                null,
                TestDuration
            );
            var recordingResult = await ExternalProcessRunner.RunAsync(
                startInfo,
                TestTimeout,
                cancellationToken,
                $"{Path.GetFileName(startInfo.FileName)} test"
            );
            if (recordingResult.ExitCode != 0)
            {
                return VideoEncoderTestResult.Unavailable(
                    profile.Codec,
                    profile.Provider,
                    videoEncoderOptions.EncoderName,
                    CreateRecordingFailureMessage(
                        profile.Provider,
                        recordingResult.StandardError,
                        recordingResult.StandardOutput,
                        recordingResult.ExitCode
                    )
                );
            }

            var validation = await ValidateOutputAsync(
                ffmpegPath,
                outputPath,
                profile.Codec,
                outputSize,
                TestDuration,
                cancellationToken
            );
            if (!validation.IsValid)
            {
                return VideoEncoderTestResult.Unavailable(
                    profile.Codec,
                    profile.Provider,
                    videoEncoderOptions.EncoderName,
                    validation.Message
                );
            }

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
        catch (Exception exception)
            when (exception
                    is Win32Exception
                        or IOException
                        or InvalidOperationException
                        or TimeoutException
            )
        {
            return VideoEncoderTestResult.Unavailable(
                profile.Codec,
                profile.Provider,
                videoEncoderOptions.EncoderName,
                SimplifyMessage(exception.Message)
            );
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    internal static IReadOnlyList<FfmpegVideoEncoderProfile> GetTestProfiles()
    {
        return FfmpegEncoderOptionsFactory.GetCalibrationProfiles();
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

    internal static async Task<FfmpegTestOutputValidation> ValidateOutputAsync(
        string ffmpegPath,
        string outputPath,
        VideoCodec codec,
        VideoCaptureSize outputSize,
        TimeSpan expectedDuration,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            return FfmpegTestOutputValidation.Invalid("No output file was produced.");
        }

        var startInfo = new ProcessStartInfo(ffmpegPath);
        foreach (
            var argument in new[]
            {
                "-hide_banner",
                "-v",
                "error",
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
                CreateFailureMessage("ffmpeg validation failed", result)
            );
        }

        return FfmpegTestOutputValidation.Valid(
            FormatCodecName(codec),
            outputSize.Width,
            outputSize.Height,
            expectedDuration.TotalSeconds
        );
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

internal sealed record FfmpegTestOutputValidation(
    bool IsValid,
    string Message,
    string? CodecName,
    int Width,
    int Height,
    double Duration
)
{
    public static FfmpegTestOutputValidation Valid(
        string codecName,
        int width,
        int height,
        double duration
    )
    {
        return new FfmpegTestOutputValidation(
            true,
            string.Empty,
            codecName,
            width,
            height,
            duration
        );
    }

    public static FfmpegTestOutputValidation Invalid(string message)
    {
        return new FfmpegTestOutputValidation(false, message, null, 0, 0, 0);
    }
}

public sealed record VideoEncoderTestResult(
    VideoCodec Codec,
    VideoEncoderProvider Provider,
    string? EncoderName,
    bool IsAvailable,
    string Message,
    int Width,
    int Height,
    double DurationSeconds
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
            durationSeconds
        );
    }

    public static VideoEncoderTestResult Unavailable(
        VideoCodec codec,
        VideoEncoderProvider provider,
        string? encoderName,
        string message
    )
    {
        return new VideoEncoderTestResult(codec, provider, encoderName, false, message, 0, 0, 0);
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
        };
    }
}

public sealed record VideoEncoderTestProgress(
    int CompletedProfiles,
    int TotalProfiles,
    VideoProfileSelection CurrentProfile
);
