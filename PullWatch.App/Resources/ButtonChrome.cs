using System.Windows;
using MediaBrush = System.Windows.Media.Brush;

namespace PullWatch;

public static class ButtonChrome
{
    public static readonly DependencyProperty HoverOverlayBrushProperty =
        DependencyProperty.RegisterAttached(
            "HoverOverlayBrush",
            typeof(MediaBrush),
            typeof(ButtonChrome),
            new PropertyMetadata(null)
        );

    public static readonly DependencyProperty PressedOverlayBrushProperty =
        DependencyProperty.RegisterAttached(
            "PressedOverlayBrush",
            typeof(MediaBrush),
            typeof(ButtonChrome),
            new PropertyMetadata(null)
        );

    public static MediaBrush? GetHoverOverlayBrush(DependencyObject element)
    {
        return (MediaBrush?)element.GetValue(HoverOverlayBrushProperty);
    }

    public static void SetHoverOverlayBrush(DependencyObject element, MediaBrush? value)
    {
        element.SetValue(HoverOverlayBrushProperty, value);
    }

    public static MediaBrush? GetPressedOverlayBrush(DependencyObject element)
    {
        return (MediaBrush?)element.GetValue(PressedOverlayBrushProperty);
    }

    public static void SetPressedOverlayBrush(DependencyObject element, MediaBrush? value)
    {
        element.SetValue(PressedOverlayBrushProperty, value);
    }
}
