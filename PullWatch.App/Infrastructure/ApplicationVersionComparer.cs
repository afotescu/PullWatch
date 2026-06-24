using NuGet.Versioning;

namespace PullWatch;

internal static class ApplicationVersionComparer
{
    public static bool IsNewer(string? candidateVersion, string? currentVersion)
    {
        return NuGetVersion.TryParse(candidateVersion, out var parsedCandidateVersion)
            && NuGetVersion.TryParse(currentVersion, out var parsedCurrentVersion)
            && VersionComparer.VersionRelease.Compare(parsedCandidateVersion, parsedCurrentVersion)
                > 0;
    }
}
