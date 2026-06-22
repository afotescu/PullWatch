namespace PullWatch;

public sealed class RecordingCategoryTab(RecordingListCategory category, string title)
    : ObservableObject
{
    private int _count;

    public RecordingListCategory Category { get; } = category;

    public string Title { get; } = title;

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string DisplayText => $"{Title} ({Count})";
}
