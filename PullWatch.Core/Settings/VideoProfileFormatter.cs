namespace PullWatch;

public static class VideoProfileFormatter
{
    public static string FormatDisplayName(VideoProfileSelection profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return $"{FormatCodecName(profile.Codec)} / {FormatProviderName(profile.Provider)}";
    }

    public static string FormatCodecName(VideoCodec codec)
    {
        return codec switch
        {
            VideoCodec.H264 => "H.264",
            VideoCodec.H265 => "H.265",
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };
    }

    public static string FormatProviderName(VideoEncoderProvider provider)
    {
        return provider switch
        {
            VideoEncoderProvider.NvidiaNvenc => "NVIDIA NVENC",
            VideoEncoderProvider.AmdAmf => "AMD AMF",
            VideoEncoderProvider.Software => "Software",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }
}
