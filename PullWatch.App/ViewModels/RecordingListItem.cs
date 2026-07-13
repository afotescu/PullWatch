using System.ComponentModel;
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
    long SizeBytes,
    bool IsFavorite = false
) : INotifyPropertyChanged
{
    public bool IsFavorite { get; private set; } = IsFavorite;

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

    public string FavoriteToolTip => IsFavorite ? "Remove from favourites" : "Add to favourites";

    public string FavoriteAutomationName => $"{FavoriteToolTip}: {Activity}";

    public event PropertyChangedEventHandler? PropertyChanged;

    // Catalog IDs keep hash-based WPF selection stable while favorite state and subscribers change.
    public bool Equals(RecordingListItem? other)
    {
        return other is not null && Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    internal void SetFavorite(bool isFavorite)
    {
        if (IsFavorite == isFavorite)
        {
            return;
        }

        IsFavorite = isFavorite;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteAutomationName)));
    }
}
