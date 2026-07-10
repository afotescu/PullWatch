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
    public Uri Source { get; } = new(Path, UriKind.Absolute);

    public bool IsPullNumberVisible => Category is RecordingListCategory.RaidEncounter;

    public bool IsContextVisible =>
        Category is RecordingListCategory.ChallengeMode or RecordingListCategory.RaidEncounter;

    public bool IsResultVisible =>
        Category is RecordingListCategory.ChallengeMode or RecordingListCategory.RaidEncounter;

    public bool IsDurationVisible => true;

    public GridLength PullNumberColumnWidth =>
        RecordingListColumnLayout.PullNumber(IsPullNumberVisible);

    public GridLength ContextColumnWidth => RecordingListColumnLayout.Context(IsContextVisible);

    public GridLength ResultColumnWidth => RecordingListColumnLayout.Result(IsResultVisible);

    public GridLength DurationColumnWidth => RecordingListColumnLayout.Duration(IsDurationVisible);

    public string FileDetail =>
        $"{ModifiedAt:yyyy-MM-dd HH:mm} · {FileSizeFormatter.Format(SizeBytes)}";

    public string ToolTip => $"{Activity}{Environment.NewLine}{FileDetail}";
}
