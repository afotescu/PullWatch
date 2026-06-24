using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public partial class App : Application
{
    private const string InstanceName = "PullWatch.Desktop.Instance";

    private ILoggerFactory? _loggerFactory;
    private InMemoryLogProvider? _logs;
    private ApplicationController? _controller;
    private ApplicationLifetimeCoordinator? _lifetime;
    private SingleInstanceCoordinator? _singleInstance;
    private TrayIconManager? _trayIcon;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logs = new InMemoryLogProvider();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        var logger = _loggerFactory.CreateLogger<App>();
        var windowsStartupShortcut = new WindowsStartupShortcut();

        logger.LogInformation("Starting PullWatch {AppVersion}", ApplicationVersion.Current);
        await ReconcileWindowsStartupShortcutAsync(windowsStartupShortcut, logger);

        _singleInstance = new SingleInstanceCoordinator(InstanceName);

        if (!_singleInstance.TryAcquire())
        {
            await _singleInstance.ActivateExistingAsync(CancellationToken.None);
            Shutdown();
            return;
        }

        _singleInstance.StartActivationListener(() =>
            Dispatcher.BeginInvoke(ShowAndActivateMainWindow)
        );
        _controller = new ApplicationController(_loggerFactory);

        try
        {
            await _controller.StartAsync(CancellationToken.None);
            _lifetime = new ApplicationLifetimeCoordinator(
                () => _controller.Status,
                ConfirmExitWhileRecording,
                _controller.FinalizeRecordingForExitAsync,
                Shutdown
            );
            _mainWindow = new MainWindow(
                _controller,
                _lifetime,
                _logs,
                windowsStartupShortcut,
                _controller.StartedWithCreatedSettingsFile
            );
            _trayIcon = new TrayIconManager(
                _controller,
                ShowAndActivateMainWindow,
                RequestExplicitExitAsync,
                _loggerFactory.CreateLogger<TrayIconManager>()
            );

            if (!ShouldStartMinimizedToTray(e.Args, _controller.Status.EffectiveSettings))
            {
                _mainWindow.Show();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "PullWatch could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _lifetime?.BeginForcedExit();

        try
        {
            if (_controller is not null)
            {
                Task.Run(() => _controller.ShutdownAsync(CancellationToken.None))
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch
        {
            // Windows is ending the session; finalization is best-effort.
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        if (_controller is not null)
        {
            Task.Run(() => _controller.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        _loggerFactory?.Dispose();

        if (_singleInstance is not null)
        {
            Task.Run(() => _singleInstance.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }

    private static bool ShouldStartMinimizedToTray(
        string[] launchArguments,
        PullWatchSettings? settings
    )
    {
        return launchArguments.Contains(
                ApplicationLaunchArguments.WindowsStartup,
                StringComparer.OrdinalIgnoreCase
            )
            && settings?.Startup.StartMinimizedToTray == true;
    }

    private static async Task ReconcileWindowsStartupShortcutAsync(
        IWindowsStartupShortcut windowsStartupShortcut,
        ILogger logger
    )
    {
        try
        {
            var loadResult = await new SettingsStore().LoadAsync(CancellationToken.None);

            if (loadResult.Status == SettingsLoadStatus.Loaded && loadResult.Settings is not null)
            {
                await windowsStartupShortcut.SyncAsync(loadResult.Settings.Startup);
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or COMException
            )
        {
            logger.LogWarning(exception, "Could not reconcile Windows startup shortcut.");
        }
    }

    private bool ConfirmExitWhileRecording()
    {
        return MessageBox.Show(
                "A recording is active. Finalize it and exit PullWatch?",
                "Exit PullWatch",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            ) == MessageBoxResult.Yes;
    }

    private async Task RequestExplicitExitAsync()
    {
        if (_lifetime is null)
        {
            return;
        }

        var exited = await _lifetime.RequestExplicitExitAsync(CancellationToken.None);

        if (
            !exited
            && _lifetime.LastFinalizationResult
                is RecordingCommandResult.Failed
                    or RecordingCommandResult.TimedOut
        )
        {
            MessageBox.Show(
                "PullWatch could not finish the active recording. The app will remain running.",
                "Could not exit PullWatch",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    private void ShowAndActivateMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }
}
