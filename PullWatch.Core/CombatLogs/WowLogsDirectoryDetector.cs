namespace PullWatch;

public static class WowLogsDirectoryDetector
{
    private static readonly string[] RelativeCandidates =
    [
        @"World of Warcraft\_retail_\Logs",
        @"Games\World of Warcraft\_retail_\Logs",
        @"Program Files\World of Warcraft\_retail_\Logs",
        @"Program Files (x86)\World of Warcraft\_retail_\Logs"
    ];

    public static string? Detect()
    {
        var drives = GetDrives();
        return drives is null
            ? null
            : Detect(CreateCandidateFactories(drives), Directory.Exists);
    }

    internal static string? Detect(
        IEnumerable<Func<WowLogsDriveCandidate?>> driveCandidates,
        Func<string, bool> directoryExists)
    {
        foreach (var getDriveCandidate in driveCandidates)
        {
            WowLogsDriveCandidate? drive;

            try
            {
                drive = getDriveCandidate();
            }
            catch (Exception exception) when (IsDriveInspectionException(exception))
            {
                continue;
            }

            if (drive is null)
            {
                continue;
            }

            foreach (var relativePath in RelativeCandidates)
            {
                var candidate = Path.Combine(drive.RootDirectory, relativePath);

                if (directoryExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static DriveInfo[]? GetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (Exception exception) when (IsDriveInspectionException(exception))
        {
            return null;
        }
    }

    private static IEnumerable<Func<WowLogsDriveCandidate?>> CreateCandidateFactories(
        IEnumerable<DriveInfo> drives)
    {
        foreach (var drive in drives)
        {
            yield return () => CreateCandidate(drive);
        }
    }

    private static WowLogsDriveCandidate? CreateCandidate(DriveInfo drive)
    {
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            return null;
        }

        return new WowLogsDriveCandidate(drive.RootDirectory.FullName);
    }

    private static bool IsDriveInspectionException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }
}

internal sealed record WowLogsDriveCandidate(string RootDirectory);
