using System.Windows;

namespace PullWatch;

public sealed record RecordingListItem(
    Guid Id,
    RecordingListCategory Category,
    string Path,
    string DisplayName,
    string StartedAt,
    string PullNumber,
    string Activity,
    string ActivityDetail,
    string Context,
    string Result,
    string Duration,
    DateTimeOffset ModifiedAt,
    long SizeBytes
)
{
    private const double PullNumberWidth = 64;
    private const double ContextWidth = 92;
    private const double ResultWidth = 92;
    private const double DurationWidth = 104;

    public Uri Source { get; } = new(Path, UriKind.Absolute);

    public bool IsPullNumberVisible => Category is RecordingListCategory.RaidEncounter;

    public bool IsContextVisible =>
        Category is RecordingListCategory.ChallengeMode or RecordingListCategory.RaidEncounter;

    public bool IsResultVisible =>
        Category is RecordingListCategory.ChallengeMode or RecordingListCategory.RaidEncounter;

    public bool IsDurationVisible => true;

    public GridLength PullNumberColumnWidth =>
        IsPullNumberVisible ? new GridLength(PullNumberWidth) : new GridLength(0);

    public GridLength ContextColumnWidth =>
        IsContextVisible ? new GridLength(ContextWidth) : new GridLength(0);

    public GridLength ResultColumnWidth =>
        IsResultVisible ? new GridLength(ResultWidth) : new GridLength(0);

    public GridLength DurationColumnWidth =>
        IsDurationVisible ? new GridLength(DurationWidth) : new GridLength(0);

    public string FileDetail => $"{ModifiedAt:yyyy-MM-dd HH:mm} · {FormatSize(SizeBytes)}";

    public string ToolTip => $"{Activity}{Environment.NewLine}{FileDetail}";

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
