using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _recordingItem;
    private readonly ApplicationController _controller;
    private readonly ILogger<TrayIconManager> _logger;
    private readonly Action _showWindow;
    private readonly Func<Task> _requestExit;

    public TrayIconManager(
        ApplicationController controller,
        Action showWindow,
        Func<Task> requestExit,
        ILogger<TrayIconManager> logger)
    {
        _controller = controller;
        _showWindow = showWindow;
        _requestExit = requestExit;
        _logger = logger;
        _recordingItem = new ToolStripMenuItem();
        _recordingItem.Click += OnRecordingClick;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PullWatch", null, (_, _) => _showWindow());
        menu.Items.Add(_recordingItem);
        menu.Items.Add("Open recordings folder", null, OnOpenRecordingsFolder);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PullWatch",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _showWindow();
        _controller.StatusChanged += OnStatusChanged;
        ApplyStatus(_controller.Status);
    }

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private async void OnRecordingClick(object? sender, EventArgs eventArgs)
    {
        await RunCommandAsync(async () =>
        {
            if (_controller.Status.Recording.State == RecordingCoordinatorState.Idle)
            {
                await _controller.StartManualRecordingAsync(CancellationToken.None);
            }
            else if (_controller.Status.Recording.State == RecordingCoordinatorState.Recording)
            {
                await _controller.StopManualRecordingAsync(CancellationToken.None);
            }
        });
    }

    private async void OnOpenRecordingsFolder(object? sender, EventArgs eventArgs)
    {
        await RunCommandAsync(async () =>
        {
            if (_controller.OperatingSystemActions is not null)
            {
                await _controller.OperatingSystemActions.OpenRecordingsFolderAsync(CancellationToken.None);
            }
        });
    }

    private async void OnExit(object? sender, EventArgs eventArgs)
    {
        await RunCommandAsync(_requestExit);
    }

    private void OnStatusChanged(ApplicationStatus status)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyStatus(status));
    }

    private void ApplyStatus(ApplicationStatus status)
    {
        _recordingItem.Text = status.Recording.State == RecordingCoordinatorState.Idle
            ? "Start manual recording"
            : "Stop recording";
        _recordingItem.Enabled = status.Recording.State is
            RecordingCoordinatorState.Idle or RecordingCoordinatorState.Recording;
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
            _notifyIcon.ShowBalloonTip(
                5000,
                "PullWatch command failed",
                exception.Message,
                ToolTipIcon.Error);
        }
    }
}
