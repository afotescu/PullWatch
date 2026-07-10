using System.Windows;

namespace PullWatch;

internal static class RecordingListColumnLayout
{
    private const double PullNumberWidth = 64;
    private const double ContextWidth = 92;
    private const double ResultWidth = 92;
    private const double DurationWidth = 104;

    public static GridLength PullNumber(bool isVisible)
    {
        return Width(isVisible, PullNumberWidth);
    }

    public static GridLength Context(bool isVisible)
    {
        return Width(isVisible, ContextWidth);
    }

    public static GridLength Result(bool isVisible)
    {
        return Width(isVisible, ResultWidth);
    }

    public static GridLength Duration(bool isVisible)
    {
        return Width(isVisible, DurationWidth);
    }

    private static GridLength Width(bool isVisible, double value)
    {
        return new GridLength(isVisible ? value : 0);
    }
}
