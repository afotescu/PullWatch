namespace PullWatch;

public sealed class NavigationItemViewModel(string title, string glyph, object content)
{
    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public object Content { get; } = content;
}
