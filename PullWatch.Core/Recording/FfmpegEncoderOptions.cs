using System.Globalization;

namespace PullWatch;

internal static class FfmpegEncoderOptionsFactory
{
    private static readonly IReadOnlyList<FfmpegVideoEncoderProfile> Profiles =
    [
        new(
            VideoCodec.H264,
            VideoEncoderProvider.NvidiaNvenc,
            "h264_nvenc",
            "H.264 / NVIDIA NVENC",
            CreateNvencArguments
        ),
        new(
            VideoCodec.H264,
            VideoEncoderProvider.AmdAmf,
            "h264_amf",
            "H.264 / AMD AMF",
            CreateAmfArguments
        ),
        new(
            VideoCodec.H264,
            VideoEncoderProvider.Software,
            "libx264",
            "H.264 / x264 software",
            CreateSoftwareArguments
        ),
        new(
            VideoCodec.H265,
            VideoEncoderProvider.NvidiaNvenc,
            "hevc_nvenc",
            "H.265 / NVIDIA NVENC",
            CreateNvencArguments
        ),
        new(
            VideoCodec.H265,
            VideoEncoderProvider.AmdAmf,
            "hevc_amf",
            "H.265 / AMD AMF",
            CreateAmfArguments
        ),
        new(
            VideoCodec.H265,
            VideoEncoderProvider.Software,
            "libx265",
            "H.265 / x265 software",
            CreateSoftwareArguments
        ),
    ];

    private static readonly IReadOnlyList<VideoProfileSelection> CalibrationOrder =
    [
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.NvidiaNvenc },
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.AmdAmf },
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.Software },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.NvidiaNvenc },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.AmdAmf },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.Software },
    ];

    private static readonly IReadOnlyList<VideoProfileSelection> SelectionPriority =
    [
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.NvidiaNvenc },
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.AmdAmf },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.NvidiaNvenc },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.AmdAmf },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.Software },
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.Software },
    ];

    public static IReadOnlyList<string> GetCandidateEncoderNames(VideoCodec codec)
    {
        return GetProfiles(codec).Select(profile => profile.EncoderName).ToArray();
    }

    public static IReadOnlyList<FfmpegVideoEncoderProfile> GetCalibrationProfiles()
    {
        return CalibrationOrder.Select(GetProfile).ToArray();
    }

    public static IReadOnlyList<FfmpegVideoEncoderProfile> GetSelectionPriorityProfiles()
    {
        return SelectionPriority.Select(GetProfile).ToArray();
    }

    public static VideoProfileSelection? SelectBestProfile(
        IEnumerable<VideoEncoderTestResult> results
    )
    {
        ArgumentNullException.ThrowIfNull(results);

        var passingProfiles = results
            .Where(result => result.IsAvailable)
            .Select(result => new VideoProfileSelection
            {
                Codec = result.Codec,
                Provider = result.Provider,
            })
            .ToHashSet();

        return SelectionPriority.FirstOrDefault(passingProfiles.Contains);
    }

    public static FfmpegVideoEncoderOptions CreateVideoEncoderOptions(
        PullWatchSettings settings,
        VideoCaptureSize outputSize,
        FfmpegEncoderCapabilities capabilities
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capabilities);

        var bitrate = VideoBitrateCalculator.CalculateBitrate(
            outputSize,
            settings.Video.FrameRate,
            settings.Video.Quality,
            settings.Video.Codec
        );
        var profile =
            GetCandidateProfiles(settings.Video.Codec, settings.Video.Encoder)
                .FirstOrDefault(profile => capabilities.Contains(profile.EncoderName))
            ?? throw CreateNoSupportedEncoderException(
                settings.Video.Codec,
                settings.Video.Encoder
            );

        return new FfmpegVideoEncoderOptions(
            settings.Video.Codec,
            profile.Provider,
            profile.DisplayName,
            profile.EncoderName,
            bitrate,
            CalculateMaxRate(bitrate),
            CalculateBufferSize(bitrate),
            profile.CreateProviderArguments(settings.Video.Quality)
        );
    }

    public static FfmpegAudioEncoderOptions CreateAudioEncoderOptions()
    {
        return new FfmpegAudioEncoderOptions(
            "aac",
            RecordingAudioDefaults.BitrateBitsPerSecond,
            RecordingAudioDefaults.SampleRate,
            RecordingAudioDefaults.Channels
        );
    }

    internal static string FormatBitrate(int bitrate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrate);

        return bitrate % 1000 == 0
            ? $"{(bitrate / 1000).ToString(CultureInfo.InvariantCulture)}k"
            : bitrate.ToString(CultureInfo.InvariantCulture);
    }

    private static IEnumerable<FfmpegVideoEncoderProfile> GetProfiles(VideoCodec codec)
    {
        if (!Enum.IsDefined(codec))
        {
            throw new ArgumentOutOfRangeException(nameof(codec), codec, null);
        }

        return Profiles.Where(profile => profile.Codec == codec);
    }

    private static IEnumerable<FfmpegVideoEncoderProfile> GetCandidateProfiles(
        VideoCodec codec,
        VideoEncoderProvider provider
    )
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }

        var profiles = GetProfiles(codec);
        return profiles.Where(profile => profile.Provider == provider);
    }

    private static FfmpegVideoEncoderProfile GetProfile(VideoProfileSelection profile)
    {
        if (!Enum.IsDefined(profile.Codec))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile.Codec, null);
        }

        if (!Enum.IsDefined(profile.Provider))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile.Provider, null);
        }

        return Profiles.Single(candidate =>
            candidate.Codec == profile.Codec && candidate.Provider == profile.Provider
        );
    }

    private static InvalidOperationException CreateNoSupportedEncoderException(
        VideoCodec codec,
        VideoEncoderProvider provider
    )
    {
        var codecName = codec switch
        {
            VideoCodec.H264 => "H.264",
            VideoCodec.H265 => "H.265",
            _ => codec.ToString(),
        };
        var candidates = string.Join(
            ", ",
            GetCandidateProfiles(codec, provider).Select(profile => profile.EncoderName)
        );

        return new InvalidOperationException(
            $"The selected FFmpeg encoder {GetProviderDisplayName(provider)} is not usable for {codecName}. Install an FFmpeg build and driver stack that supports: {candidates}, or choose another video encoder in settings."
        );
    }

    private static string GetProviderDisplayName(VideoEncoderProvider provider)
    {
        return provider switch
        {
            VideoEncoderProvider.NvidiaNvenc => "NVIDIA NVENC",
            VideoEncoderProvider.AmdAmf => "AMD AMF",
            VideoEncoderProvider.Software => "Software",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    private static int CalculateMaxRate(int bitrate)
    {
        return bitrate + bitrate / 2;
    }

    private static int CalculateBufferSize(int bitrate)
    {
        return bitrate * 2;
    }

    private static IReadOnlyList<string> CreateNvencArguments(VideoQuality quality)
    {
        return
        [
            "-preset",
            "p4",
            "-rc",
            "vbr",
            "-cq",
            GetNvencConstantQuality(quality).ToString(CultureInfo.InvariantCulture),
            "-bf",
            "0",
            "-surfaces",
            "8",
        ];
    }

    private static IReadOnlyList<string> CreateAmfArguments(VideoQuality quality)
    {
        return
        [
            "-usage",
            "lowlatency_high_quality",
            "-rc",
            "vbr_peak",
            "-quality",
            GetAmfQualityPreset(quality),
        ];
    }

    private static IReadOnlyList<string> CreateSoftwareArguments(VideoQuality quality)
    {
        return ["-preset", GetSoftwarePreset(quality)];
    }

    private static int GetNvencConstantQuality(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Compact => 23,
            VideoQuality.Balanced => 20,
            VideoQuality.High => 17,
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
        };
    }

    private static string GetAmfQualityPreset(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Compact => "speed",
            VideoQuality.Balanced => "balanced",
            VideoQuality.High => "quality",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
        };
    }

    private static string GetSoftwarePreset(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Compact => "veryfast",
            VideoQuality.Balanced => "fast",
            VideoQuality.High => "medium",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
        };
    }
}

internal sealed record FfmpegVideoEncoderOptions(
    VideoCodec Codec,
    VideoEncoderProvider Provider,
    string DisplayName,
    string EncoderName,
    int Bitrate,
    int MaxRate,
    int BufferSize,
    IReadOnlyList<string> ProviderArguments
)
{
    public IReadOnlyList<string> CreateArguments()
    {
        var arguments = new List<string>
        {
            "-c:v",
            EncoderName,
            "-b:v",
            FfmpegEncoderOptionsFactory.FormatBitrate(Bitrate),
            "-maxrate",
            FfmpegEncoderOptionsFactory.FormatBitrate(MaxRate),
            "-bufsize",
            FfmpegEncoderOptionsFactory.FormatBitrate(BufferSize),
        };

        arguments.AddRange(ProviderArguments);
        return arguments;
    }
}

internal sealed record FfmpegAudioInputOptions(
    string FfmpegFormat,
    int SampleRate,
    int Channels,
    string PipePath
);

internal sealed record FfmpegAudioEncoderOptions(
    string CodecName,
    int Bitrate,
    int SampleRate,
    int Channels
)
{
    public IReadOnlyList<string> CreateArguments()
    {
        return
        [
            "-c:a",
            CodecName,
            "-b:a",
            FfmpegEncoderOptionsFactory.FormatBitrate(Bitrate),
            "-ar",
            SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac",
            Channels.ToString(CultureInfo.InvariantCulture),
        ];
    }
}

internal sealed record FfmpegVideoEncoderProfile(
    VideoCodec Codec,
    VideoEncoderProvider Provider,
    string EncoderName,
    string DisplayName,
    Func<VideoQuality, IReadOnlyList<string>> CreateProviderArguments
);
