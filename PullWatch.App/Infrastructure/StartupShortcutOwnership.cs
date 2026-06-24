using System.IO;
using NuGet.Versioning;

namespace PullWatch;

internal enum StartupShortcutOwnershipAction
{
    DeleteShortcut,
    WriteCurrentShortcut,
    KeepExistingShortcut,
}

internal sealed record StartupShortcutInspection(
    bool ShortcutExists,
    string? TargetPath,
    bool TargetExists,
    string? TargetVersion,
    bool IsPullWatchTarget
)
{
    public static StartupShortcutInspection Missing { get; } = new(false, null, false, null, false);
}

internal static class StartupShortcutOwnership
{
    public static StartupShortcutOwnershipAction Decide(
        bool startWithWindows,
        StartupShortcutInspection existingShortcut,
        string currentExecutablePath,
        string? currentVersion
    )
    {
        if (!startWithWindows)
        {
            return StartupShortcutOwnershipAction.DeleteShortcut;
        }

        if (
            existingShortcut.ShortcutExists
            && !string.IsNullOrWhiteSpace(existingShortcut.TargetPath)
            && PathsMatch(existingShortcut.TargetPath, currentExecutablePath)
        )
        {
            return StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        if (IsNonClaimingDevelopmentVersion(currentVersion))
        {
            return StartupShortcutOwnershipAction.KeepExistingShortcut;
        }

        if (!existingShortcut.ShortcutExists)
        {
            return StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        if (string.IsNullOrWhiteSpace(existingShortcut.TargetPath))
        {
            return StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        if (!existingShortcut.TargetExists)
        {
            return StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        if (!existingShortcut.IsPullWatchTarget)
        {
            return StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        if (!NuGetVersion.TryParse(currentVersion, out var parsedCurrentVersion))
        {
            return NuGetVersion.TryParse(existingShortcut.TargetVersion, out _)
                ? StartupShortcutOwnershipAction.KeepExistingShortcut
                : StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        if (!NuGetVersion.TryParse(existingShortcut.TargetVersion, out var parsedTargetVersion))
        {
            return StartupShortcutOwnershipAction.WriteCurrentShortcut;
        }

        return
            VersionComparer.VersionRelease.Compare(parsedCurrentVersion, parsedTargetVersion) >= 0
            ? StartupShortcutOwnershipAction.WriteCurrentShortcut
            : StartupShortcutOwnershipAction.KeepExistingShortcut;
    }

    private static bool IsNonClaimingDevelopmentVersion(string? version)
    {
        return string.Equals(version?.Trim(), "0.0.0-dev", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsMatch(string left, string right)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(
            NormalizePathForComparison(left),
            NormalizePathForComparison(right)
        );
    }

    private static string NormalizePathForComparison(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
