namespace PullWatch.Tests;

public sealed class FfmpegRecordingServiceTests
{
    [Fact]
    public void StartInfoUsesNvencWhenItIsUsableForSelectedH264Codec()
    {
        var settings = CreateSettings(
            VideoCodec.H264,
            VideoQuality.Balanced,
            VideoEncoderProvider.NvidiaNvenc
        );
        var captureSize = new VideoCaptureSize(2560, 1440);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var startInfo = FfmpegRecordingService.CreateStartInfo(
            "ffmpeg",
            new nint(1234),
            settings,
            captureSize,
            outputSize,
            @"D:\Recordings\h264.mp4",
            null,
            Capabilities("h264_nvenc", "h264_amf", "libx264")
        );

        var arguments = startInfo.ArgumentList.ToArray();

        AssertArgumentValue(arguments, "-c:v", "h264_nvenc");
        AssertArgumentValue(arguments, "-b:v", "9000k");
        AssertArgumentValue(arguments, "-maxrate", "13500k");
        AssertArgumentValue(arguments, "-bufsize", "18000k");
        AssertArgumentValue(arguments, "-rc", "vbr");
        AssertArgumentValue(arguments, "-cq", "20");
        AssertArgumentValue(arguments, "-bf", "0");
        AssertArgumentValue(arguments, "-surfaces", "8");
        Assert.DoesNotContain("hwdownload", GetArgumentValue(arguments, "-filter_complex"));
        Assert.Contains("-an", arguments);
        Assert.DoesNotContain("-c:a", arguments);
        Assert.Equal(@"D:\Recordings\h264.mp4", arguments[^1]);
    }

    [Fact]
    public void StartInfoDoesNotFallBackWhenSelectedProviderIsUnavailable()
    {
        var settings = CreateSettings(
            VideoCodec.H264,
            VideoQuality.High,
            VideoEncoderProvider.NvidiaNvenc
        );
        var captureSize = new VideoCaptureSize(1920, 1080);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FfmpegRecordingService.CreateStartInfo(
                "ffmpeg",
                new nint(1234),
                settings,
                captureSize,
                outputSize,
                @"D:\Recordings\missing.mp4",
                null,
                Capabilities("h264_amf", "libx264")
            )
        );

        Assert.Contains("selected FFmpeg encoder NVIDIA NVENC is not usable", exception.Message);
        Assert.Contains("h264_nvenc", exception.Message);
        Assert.DoesNotContain("h264_amf", exception.Message);
    }

    [Fact]
    public void StartInfoUsesSoftwareEncoderWhenSelected()
    {
        var settings = CreateSettings(
            VideoCodec.H264,
            VideoQuality.Balanced,
            VideoEncoderProvider.Software
        );
        var captureSize = new VideoCaptureSize(1920, 1080);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var startInfo = FfmpegRecordingService.CreateStartInfo(
            "ffmpeg",
            new nint(1234),
            settings,
            captureSize,
            outputSize,
            @"D:\Recordings\x264.mp4",
            null,
            Capabilities("libx264")
        );

        var arguments = startInfo.ArgumentList.ToArray();

        AssertArgumentValue(arguments, "-c:v", "libx264");
        AssertArgumentValue(arguments, "-preset", "fast");
        Assert.Contains(
            ",hwdownload,format=bgra,format=yuv420p[v]",
            GetArgumentValue(arguments, "-filter_complex")
        );
        Assert.DoesNotContain("-cq", arguments);
        Assert.DoesNotContain("-rc", arguments);
    }

    [Fact]
    public void StartInfoNormalizesOddOriginalDimensionsForFfmpegEncoders()
    {
        var settings = CreateSettings(
            VideoCodec.H264,
            VideoQuality.Balanced,
            VideoEncoderProvider.Software,
            VideoScaling.Original
        );
        var captureSize = new VideoCaptureSize(2560, 1351);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var startInfo = FfmpegRecordingService.CreateStartInfo(
            "ffmpeg",
            new nint(1234),
            settings,
            captureSize,
            outputSize,
            @"D:\Recordings\x264.mp4",
            null,
            Capabilities("libx264")
        );

        var filter = GetArgumentValue(startInfo.ArgumentList.ToArray(), "-filter_complex");

        Assert.Contains(":width=2560:height=1350:resize_mode=scale_aspect", filter);
        Assert.Contains(",hwdownload,format=bgra,format=yuv420p[v]", filter);
    }

    [Fact]
    public void StartInfoUsesExplicitProviderInsteadOfAutoPriority()
    {
        var settings = CreateSettings(
            VideoCodec.H264,
            VideoQuality.Balanced,
            VideoEncoderProvider.AmdAmf
        );
        var captureSize = new VideoCaptureSize(1920, 1080);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var startInfo = FfmpegRecordingService.CreateStartInfo(
            "ffmpeg",
            new nint(1234),
            settings,
            captureSize,
            outputSize,
            @"D:\Recordings\amf.mp4",
            null,
            Capabilities("h264_nvenc", "h264_amf", "libx264")
        );

        var arguments = startInfo.ArgumentList.ToArray();

        AssertArgumentValue(arguments, "-c:v", "h264_amf");
        AssertArgumentValue(arguments, "-rc", "vbr_peak");
    }

    [Fact]
    public void StartInfoReportsSelectedProviderWhenExplicitProviderIsUnavailable()
    {
        var settings = CreateSettings(
            VideoCodec.H265,
            VideoQuality.Balanced,
            VideoEncoderProvider.NvidiaNvenc
        );
        var captureSize = new VideoCaptureSize(1920, 1080);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FfmpegRecordingService.CreateStartInfo(
                "ffmpeg",
                new nint(1234),
                settings,
                captureSize,
                outputSize,
                @"D:\Recordings\missing.mp4",
                null,
                Capabilities("hevc_amf", "libx265")
            )
        );

        Assert.Contains("selected FFmpeg encoder NVIDIA NVENC is not usable", exception.Message);
        Assert.Contains("hevc_nvenc", exception.Message);
        Assert.Contains("choose another video encoder in settings", exception.Message);
    }

    [Fact]
    public void StartInfoUsesSelectedH265CodecCandidateList()
    {
        var settings = CreateSettings(
            VideoCodec.H265,
            VideoQuality.Balanced,
            VideoEncoderProvider.AmdAmf
        );
        var captureSize = new VideoCaptureSize(2560, 1440);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var startInfo = FfmpegRecordingService.CreateStartInfo(
            "ffmpeg",
            new nint(1234),
            settings,
            captureSize,
            outputSize,
            @"D:\Recordings\h265.mp4",
            null,
            Capabilities("hevc_amf", "libx265", "h264_nvenc")
        );

        var arguments = startInfo.ArgumentList.ToArray();

        AssertArgumentValue(arguments, "-c:v", "hevc_amf");
        AssertArgumentValue(arguments, "-b:v", "5000k");
        AssertArgumentValue(arguments, "-maxrate", "7500k");
        AssertArgumentValue(arguments, "-bufsize", "10000k");
        AssertArgumentValue(arguments, "-rc", "vbr_peak");
    }

    [Fact]
    public void StartInfoReportsSelectedCodecWhenNoEncoderIsUsable()
    {
        var settings = CreateSettings(
            VideoCodec.H265,
            VideoQuality.Balanced,
            VideoEncoderProvider.Software
        );
        var captureSize = new VideoCaptureSize(1920, 1080);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FfmpegRecordingService.CreateStartInfo(
                "ffmpeg",
                new nint(1234),
                settings,
                captureSize,
                outputSize,
                @"D:\Recordings\missing.mp4",
                null,
                Capabilities("h264_nvenc", "libx264")
            )
        );

        Assert.Contains(
            "selected FFmpeg encoder Software is not usable for H.265",
            exception.Message
        );
        Assert.Contains("libx265", exception.Message);
    }

    [Fact]
    public void StartInfoUsesRecorderAudioDefaultsWhenAudioIsEnabled()
    {
        var settings = new PullWatchSettings
        {
            Video = new VideoSettings
            {
                SelectedProfile = new VideoProfileSelection
                {
                    Codec = VideoCodec.H264,
                    Provider = VideoEncoderProvider.Software,
                },
            },
            Audio = new AudioSettings { CaptureSystemAudio = true },
        };
        var captureSize = new VideoCaptureSize(1920, 1080);
        var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);
        var audioInput = new FfmpegAudioInputOptions(
            "f32le",
            44100,
            2,
            @"\\.\pipe\PullWatchAudio-Test"
        );

        var startInfo = FfmpegRecordingService.CreateStartInfo(
            "ffmpeg",
            new nint(1234),
            settings,
            captureSize,
            outputSize,
            @"D:\Recordings\audio.mp4",
            audioInput,
            Capabilities("libx264")
        );

        var arguments = startInfo.ArgumentList.ToArray();

        AssertArgumentValue(arguments, "-f", "f32le");
        AssertArgumentValue(arguments, "-i", @"\\.\pipe\PullWatchAudio-Test");
        Assert.Contains("0:a:0", arguments);
        AssertArgumentValue(arguments, "-c:a", "aac");
        AssertArgumentValue(arguments, "-b:a", "96k");
        Assert.Equal(
            RecordingAudioDefaults.SampleRate.ToString(),
            GetLastArgumentValue(arguments, "-ar")
        );
        Assert.Equal(
            RecordingAudioDefaults.Channels.ToString(),
            GetLastArgumentValue(arguments, "-ac")
        );
    }

    [Fact]
    public void EncoderCapabilitiesParseFfmpegEncoderListOutput()
    {
        const string output = """
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             A..... aac                  AAC (Advanced Audio Coding)
             V..... h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             V....D hevc_amf             AMD AMF HEVC encoder (codec hevc)
            """;

        var capabilities = FfmpegEncoderCapabilities.Parse(output);

        Assert.True(capabilities.Contains("libx264"));
        Assert.True(capabilities.Contains("H264_NVENC"));
        Assert.True(capabilities.Contains("hevc_amf"));
        Assert.False(capabilities.Contains("aac"));
    }

    [Fact]
    public void EncoderCalibrationProfilesUseExpectedOrder()
    {
        var profiles = FfmpegEncoderTestService.GetTestProfiles();

        Assert.Equal(
            [
                (VideoCodec.H265, VideoEncoderProvider.NvidiaNvenc),
                (VideoCodec.H265, VideoEncoderProvider.AmdAmf),
                (VideoCodec.H265, VideoEncoderProvider.Software),
                (VideoCodec.H264, VideoEncoderProvider.NvidiaNvenc),
                (VideoCodec.H264, VideoEncoderProvider.AmdAmf),
                (VideoCodec.H264, VideoEncoderProvider.Software),
            ],
            profiles.Select(profile => (profile.Codec, profile.Provider)).ToArray()
        );
    }

    [Fact]
    public void EncoderSelectionPriorityPrefersH265NvencOverH264Nvenc()
    {
        var selected = FfmpegEncoderOptionsFactory.SelectBestProfile([
            VideoEncoderTestResult.Available(
                VideoCodec.H264,
                VideoEncoderProvider.NvidiaNvenc,
                "h264_nvenc",
                "ok",
                1920,
                1080,
                2.0
            ),
            VideoEncoderTestResult.Available(
                VideoCodec.H265,
                VideoEncoderProvider.NvidiaNvenc,
                "hevc_nvenc",
                "ok",
                1920,
                1080,
                2.0
            ),
        ]);

        Assert.Equal(
            new VideoProfileSelection
            {
                Codec = VideoCodec.H265,
                Provider = VideoEncoderProvider.NvidiaNvenc,
            },
            selected
        );
    }

    [Fact]
    public void EncoderSelectionPriorityPrefersH264SoftwareOverH265Software()
    {
        var selected = FfmpegEncoderOptionsFactory.SelectBestProfile([
            VideoEncoderTestResult.Available(
                VideoCodec.H265,
                VideoEncoderProvider.Software,
                "libx265",
                "ok",
                1920,
                1080,
                2.0
            ),
            VideoEncoderTestResult.Available(
                VideoCodec.H264,
                VideoEncoderProvider.Software,
                "libx264",
                "ok",
                1920,
                1080,
                2.0
            ),
        ]);

        Assert.Equal(
            new VideoProfileSelection
            {
                Codec = VideoCodec.H264,
                Provider = VideoEncoderProvider.Software,
            },
            selected
        );
    }

    [Fact]
    public void EncoderSelectionPriorityReturnsNoProfileWhenAllTestsFail()
    {
        var selected = FfmpegEncoderOptionsFactory.SelectBestProfile([
            VideoEncoderTestResult.Unavailable(
                VideoCodec.H265,
                VideoEncoderProvider.NvidiaNvenc,
                "hevc_nvenc",
                "failed"
            ),
            VideoEncoderTestResult.Unavailable(
                VideoCodec.H264,
                VideoEncoderProvider.Software,
                "libx264",
                "failed"
            ),
        ]);

        Assert.Null(selected);
    }

    [Fact]
    public void EncoderTestFailureDetailPrefersSpecificFfmpegErrorsOverMappingNoise()
    {
        const string stderr = """
            Stream mapping:
              Stream #0:0 (wrapped_avframe) -> fps:default
              format:default -> Stream #0:0 (libx264)
            Press [q] to stop, [?] for help
            [libx264 @ 000001] height not divisible by 2 (2560x1351)
            Error initializing output stream 0:0 -- Error while opening encoder
            Conversion failed!
            """;

        var detail = FfmpegEncoderTestService.SelectFailureDetail(stderr, string.Empty);

        Assert.Equal("Error initializing output stream 0:0 -- Error while opening encoder", detail);
    }

    [Fact]
    public void EncoderTestRecordingFailureSummarizesHardwareProbeRejection()
    {
        const string stderr =
            "[vost#0:0/h264_amf @ 000001] Terminating thread with return code -22 (Invalid argument)";

        var message = FfmpegEncoderTestService.CreateRecordingFailureMessage(
            VideoEncoderProvider.AmdAmf,
            stderr,
            string.Empty,
            -22
        );

        Assert.Equal(
            "recording failed; encoder is present in FFmpeg, but the current hardware or driver stack rejected the test encode.",
            message
        );
    }

    [Fact]
    public async Task EncoderTestValidationToolFailureIsNotReportedAsProviderUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"PullWatch-ffprobe-missing-{Guid.NewGuid():N}.mp4"
        );
        await File.WriteAllTextAsync(outputPath, "not empty", cancellationToken);

        try
        {
            var exception = await Assert.ThrowsAsync<FfmpegEncoderTestValidationException>(() =>
                FfmpegEncoderTestService.ValidateOutputAsync(
                    $"missing-ffprobe-{Guid.NewGuid():N}.exe",
                    outputPath,
                    cancellationToken
                )
            );

            Assert.Contains("ffprobe validation could not run", exception.Message);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static PullWatchSettings CreateSettings(
        VideoCodec codec,
        VideoQuality quality,
        VideoEncoderProvider encoderProvider = VideoEncoderProvider.Software,
        VideoScaling scaling = VideoScaling.Optimized
    )
    {
        return new PullWatchSettings
        {
            Video = new VideoSettings
            {
                SelectedProfile = new VideoProfileSelection
                {
                    Codec = codec,
                    Provider = encoderProvider,
                },
                Quality = quality,
                FrameRate = VideoFrameRates.High,
                Scaling = scaling,
            },
            Audio = new AudioSettings { CaptureSystemAudio = false },
        };
    }

    private static FfmpegEncoderCapabilities Capabilities(params string[] encoderNames)
    {
        return new FfmpegEncoderCapabilities(encoderNames);
    }

    private static void AssertArgumentValue(
        IReadOnlyList<string> arguments,
        string name,
        string expectedValue
    )
    {
        Assert.Equal(expectedValue, GetArgumentValue(arguments, name));
    }

    private static string GetArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = Array.IndexOf(arguments.ToArray(), name);

        Assert.True(index >= 0, $"Expected argument '{name}' to be present.");
        Assert.True(index + 1 < arguments.Count, $"Expected argument '{name}' to have a value.");

        return arguments[index + 1];
    }

    private static string GetLastArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = Array.LastIndexOf(arguments.ToArray(), name);

        Assert.True(index >= 0, $"Expected argument '{name}' to be present.");
        Assert.True(index + 1 < arguments.Count, $"Expected argument '{name}' to have a value.");

        return arguments[index + 1];
    }
}
