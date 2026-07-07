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
        menu.Items.Add(new Separator());
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
        var menu = new ContextMenu
        {
            Background = SystemColors.MenuBrush,
            Foreground = SystemColors.MenuTextBrush,
        };
        menu.Resources.Add(typeof(TextBlock), CreateMenuTextBlockStyle());
        menu.Resources.Add(typeof(MenuItem), CreateMenuItemStyle());
        return menu;
    }

    private static MenuItem CreateMenuItem(string text, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = text };
        item.Click += click;
        return item;
    }

    private static Style CreateMenuTextBlockStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, SystemColors.MenuTextBrush));
        return style;
    }

    private static Style CreateMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.MenuTextBrush));
        return style;
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
