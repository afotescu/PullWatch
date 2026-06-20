namespace PullWatch;

public sealed record RecordingListItem(
    Guid Id,
    string Path,
    string DisplayName,
    DateTimeOffset ModifiedAt,
    long SizeBytes
)
{
    public Uri Source { get; } = new(Path, UriKind.Absolute);

    public string Detail => $"{ModifiedAt:yyyy-MM-dd HH:mm} · {FormatSize(SizeBytes)}";

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):0.0} MB";
        }

        return $"{Math.Max(0, bytes / 1024d):0.0} KB";
    }
}
