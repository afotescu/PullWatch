using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class TrayIconManager : IDisposable
{
    private const string SurfaceRaisedBrushKey = "SurfaceRaisedBrush";
    private const string PrimaryTextBrushKey = "PrimaryTextBrush";
    private const string SecondaryTextBrushKey = "SecondaryTextBrush";
    private const string BorderBrushKey = "BorderBrush";
    private const string ListItemHoverBrushKey = "ListItemHoverBrush";

    private static readonly Uri TrayIconUri = new(
        "pack://application:,,,/Assets/favicon.ico",
        UriKind.Absolute
    );

    private readonly TaskbarIcon _taskbarIcon;
    private readonly MenuItem _recordingItem;
    private readonly ApplicationController _controller;
    private readonly ILogger<TrayIconManager> _logger;
    private readonly Action _showWindow;
    private readonly Func<Task> _requestExit;

    public TrayIconManager(
        ApplicationController controller,
        Action showWindow,
        Func<Task> requestExit,
        ILogger<TrayIconManager> logger
    )
    {
        _controller = controller;
        _showWindow = showWindow;
        _requestExit = requestExit;
        _logger = logger;
        _recordingItem = CreateMenuItem(string.Empty, OnRecordingClick);

        var menu = CreateContextMenu();
        var openItem = CreateMenuItem("Open PullWatch", (_, _) => _showWindow());
        menu.Items.Add(openItem);
        menu.Items.Add(_recordingItem);
        var recordingsFolderItem = CreateMenuItem("Open recordings folder", OnOpenRecordingsFolder);
        menu.Items.Add(recordingsFolderItem);
        var exitItem = CreateMenuItem("Exit", OnExit);
        menu.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "PullWatch",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        if (LoadTrayIconSource(_logger) is { } iconSource)
        {
            _taskbarIcon.IconSource = iconSource;
        }

        _taskbarIcon.TrayLeftMouseDoubleClick += OnTrayLeftMouseDoubleClick;
        _taskbarIcon.ForceCreate();
        _controller.StatusChanged += OnStatusChanged;
        ApplyStatus(_controller.Status);
    }

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _taskbarIcon.TrayLeftMouseDoubleClick -= OnTrayLeftMouseDoubleClick;
        _recordingItem.Click -= OnRecordingClick;
        _taskbarIcon.Dispose();
    }

    private async void OnRecordingClick(object? sender, RoutedEventArgs eventArgs)
    {
        await RunCommandAsync(async () =>
        {
            if (_controller.Status.Recording.State == RecordingCoordinatorState.Idle)
            {
                await _controller.StartManualRecordingAsync();
            }
            else if (_controller.Status.Recording.State == RecordingCoordinatorState.Recording)
            {
                await _controller.StopManualRecordingAsync();
            }
        });
    }

    private async void OnOpenRecordingsFolder(object? sender, RoutedEventArgs eventArgs)
    {
        await RunCommandAsync(async () =>
        {
            await _controller.OpenRecordingsFolderAsync();
        });
    }

    private async void OnExit(object? sender, RoutedEventArgs eventArgs)
    {
        await RunCommandAsync(_requestExit);
    }

    private void OnTrayLeftMouseDoubleClick(object sender, RoutedEventArgs eventArgs)
    {
        _showWindow();
    }

    private void OnStatusChanged(ApplicationStatus status)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyStatus(status));
    }

    private void ApplyStatus(ApplicationStatus status)
    {
        _recordingItem.Header =
            status.Recording.State == RecordingCoordinatorState.Idle
                ? "Start manual recording"
                : "Stop recording";
        _recordingItem.IsEnabled =
            status.Recording.State
                is RecordingCoordinatorState.Idle
                    or RecordingCoordinatorState.Recording;
    }

    private static ContextMenu CreateContextMenu()
    {
        var backgroundBrush = GetBrush(SurfaceRaisedBrushKey, Color.FromRgb(0x2A, 0x30, 0x38));
        var foregroundBrush = GetBrush(PrimaryTextBrushKey, Color.FromRgb(0xF2, 0xF4, 0xF7));
        var disabledForegroundBrush = GetBrush(
            SecondaryTextBrushKey,
            Color.FromRgb(0xAA, 0xB2, 0xBD)
        );
        var borderBrush = GetBrush(BorderBrushKey, Color.FromRgb(0x40, 0x49, 0x56));
        var hoverBrush = GetBrush(ListItemHoverBrushKey, Color.FromRgb(0x30, 0x37, 0x42));

        var menu = new ContextMenu
        {
            Background = backgroundBrush,
            BorderBrush = borderBrush,
            Foreground = foregroundBrush,
            Padding = new Thickness(4),
            Template = CreateContextMenuTemplate(),
        };
        menu.Resources.Add(typeof(TextBlock), CreateMenuTextBlockStyle(foregroundBrush));
        menu.Resources.Add(
            typeof(MenuItem),
            CreateMenuItemStyle(foregroundBrush, disabledForegroundBrush, hoverBrush)
        );
        return menu;
    }

    private static ControlTemplate CreateContextMenuTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty)
        );
        root.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty)
        );
        root.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        root.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty)
        );
        root.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        items.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        root.AppendChild(items);

        return new ControlTemplate(typeof(ContextMenu)) { VisualTree = root };
    }

    private static MenuItem CreateMenuItem(string text, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = text };
        item.Click += click;
        return item;
    }

    private static Style CreateMenuTextBlockStyle(Brush foregroundBrush)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, foregroundBrush));
        return style;
    }

    private static Style CreateMenuItemStyle(
        Brush foregroundBrush,
        Brush disabledForegroundBrush,
        Brush hoverBrush
    )
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foregroundBrush));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 7, 24, 7)));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateMenuItemTemplate(hoverBrush)));

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(
            new Setter(Control.ForegroundProperty, disabledForegroundBrush)
        );
        style.Triggers.Add(disabledTrigger);

        return style;
    }

    private static ControlTemplate CreateMenuItemTemplate(Brush hoverBrush)
    {
        var root = new FrameworkElementFactory(typeof(Border)) { Name = "Root" };
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.SetValue(
            Border.PaddingProperty,
            new TemplateBindingExtension(Control.PaddingProperty)
        );

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(content);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };
        var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "Root"));
        template.Triggers.Add(hoverTrigger);

        return template;
    }

    private static Brush GetBrush(string resourceKey, Color fallbackColor)
    {
        return System.Windows.Application.Current.TryFindResource(resourceKey) as Brush
            ?? new SolidColorBrush(fallbackColor);
    }

    private static ImageSource? LoadTrayIconSource(ILogger logger)
    {
        try
        {
            var iconSource = BitmapFrame.Create(TrayIconUri);
            if (iconSource.CanFreeze)
            {
                iconSource.Freeze();
            }

            return iconSource;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to load tray icon resource");
            return null;
        }
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tray command failed");
            _taskbarIcon.ShowNotification(
                "PullWatch command failed",
                exception.Message,
                NotificationIcon.Error,
                timeout: TimeSpan.FromSeconds(5)
            );
        }
    }
}
