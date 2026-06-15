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
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            foreach (var relativePath in RelativeCandidates)
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, relativePath);

                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
