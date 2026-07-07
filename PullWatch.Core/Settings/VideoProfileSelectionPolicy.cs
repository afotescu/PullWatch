namespace PullWatch;

public static class VideoProfileSelectionPolicy
{
    private static readonly IReadOnlyList<VideoProfileSelection> SelectionPriority =
    [
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.NvidiaNvenc },
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.AmdAmf },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.NvidiaNvenc },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.AmdAmf },
        new() { Codec = VideoCodec.H264, Provider = VideoEncoderProvider.Software },
        new() { Codec = VideoCodec.H265, Provider = VideoEncoderProvider.Software },
    ];

    public static VideoProfileSelection? SelectBestPassingProfile(
        IEnumerable<EncoderCalibrationResult> results
    )
    {
        ArgumentNullException.ThrowIfNull(results);

        return GetPassingProfilesInPriorityOrder(results).FirstOrDefault();
    }

    public static VideoProfileSelection? SelectBestPassingProfile(
        IEnumerable<VideoProfileSelection> passingProfiles
    )
    {
        ArgumentNullException.ThrowIfNull(passingProfiles);

        var passingSet = passingProfiles.ToHashSet();
        return SelectionPriority.FirstOrDefault(passingSet.Contains);
    }

    public static IReadOnlyList<VideoProfileSelection> GetPassingProfilesInPriorityOrder(
        IEnumerable<EncoderCalibrationResult> results
    )
    {
        ArgumentNullException.ThrowIfNull(results);

        return GetProfilesInPriorityOrder(
            results
                .Where(result => result.Passed)
                .Select(result => new VideoProfileSelection
                {
                    Codec = result.Codec,
                    Provider = result.Provider,
                })
        );
    }

    public static IReadOnlyList<VideoProfileSelection> GetProfilesInPriorityOrder(
        IEnumerable<VideoProfileSelection> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var profileSet = profiles.ToHashSet();
        return SelectionPriority.Where(profileSet.Contains).ToArray();
    }
}
