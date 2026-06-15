namespace PullWatch;

public sealed class PlaceholderViewModel(string title, string description)
{
    public string Title { get; } = title;

    public string Description { get; } = description;
}
