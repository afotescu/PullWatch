using System.Windows.Media;

namespace PullWatch;

public sealed class NavigationItemViewModel(string title, Geometry icon, object content)
{
    public string Title { get; } = title;

    public Geometry Icon { get; } = icon;

    public object Content { get; } = content;
}
