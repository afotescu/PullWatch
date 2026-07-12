using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Velopack;

namespace PullWatch;

public partial class App : Application
{
    private const string InstanceName = "PullWatch.Desktop.Instance";
    private static readonly TimeSpan UpgradeReleaseTimeout = TimeSpan.FromSeconds(60);

    private ILoggerFactory? _loggerFactory;
    private InMemoryLogProvider? _logs;
    private ApplicationController? _controller;
    private ApplicationLifetimeCoordinator? _lifetime;
    private SingleInstanceCoordinator? _singleInstance;
    private TrayIconManager? _trayIcon;
    private MainWindow? _mainWindow;
    private int _upgradeShutdownStarted;

    internal SemanticVersion? RestartedVersion { get; init; }

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
            var activationResult = await _singleInstance.ActivateExistingAsync(
                CreateLaunchRequest(),
                CancellationToken.None
            );

            if (activationResult != SingleInstanceActivationResult.UpgradeAccepted)
            {
                Shutdown();
                return;
            }

            if (
                !await _singleInstance.WaitForReleaseAndAcquireAsync(
                    UpgradeReleaseTimeout,
                    CancellationToken.None
                )
            )
            {
                MessageBox.Show(
                    "The running PullWatch instance accepted the upgrade, but did not exit in time. Close PullWatch and open this version again.",
                    "PullWatch upgrade could not continue",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                Shutdown(1);
                return;
            }
        }

        var applicationUpdater = new VelopackApplicationUpdater();

        if (
            StartupUpdateInstaller.TryApplyPendingUpdateAndRestart(
                applicationUpdater,
                Shutdown,
                logger
            )
        )
        {
            return;
        }

        _singleInstance.StartActivationListener(
            HandleSingleInstanceActivationAsync,
            CompleteSingleInstanceActivationExchange
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
                _loggerFactory.CreateLogger<FfmpegEncoderTestService>(),
                windowsStartupShortcut,
                applicationUpdater,
                _controller.StartedWithCreatedSettingsFile
            );
            var whatsNewViewModel = CreateWhatsNewViewModel(applicationUpdater, logger);
            _trayIcon = new TrayIconManager(
                _controller,
                ShowAndActivateMainWindow,
                RequestExplicitExitAsync,
                _loggerFactory.CreateLogger<TrayIconManager>()
            );

            if (
                whatsNewViewModel is not null
                || !ShouldStartMinimizedToTray(e.Args, _controller.Status.EffectiveSettings)
            )
            {
                _mainWindow.Show();
            }

            if (whatsNewViewModel is not null)
            {
                _mainWindow.ShowWhatsNew(whatsNewViewModel);
            }

            await _mainWindow.PromptForEncoderCalibrationIfNeededAsync(CancellationToken.None);
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

    private WhatsNewViewModel? CreateWhatsNewViewModel(
        VelopackApplicationUpdater applicationUpdater,
        ILogger logger
    )
    {
        if (RestartedVersion is null)
        {
            return null;
        }

        try
        {
            var release = applicationUpdater.GetCurrentRelease(RestartedVersion);
            return release is null
                ? null
                : WhatsNewViewModel.Create(release.Version, release.ReleaseNotesMarkdown);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load release notes for the installed update.");
            return null;
        }
    }

    private static SingleInstanceLaunchRequest CreateLaunchRequest()
    {
        string? executablePath = null;

        try
        {
            executablePath = string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? null
                : Path.GetFullPath(Environment.ProcessPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            executablePath = Environment.ProcessPath;
        }

        return new SingleInstanceLaunchRequest(ApplicationVersion.Current, executablePath);
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

    private Task<SingleInstanceActivationResult> HandleSingleInstanceActivationAsync(
        SingleInstanceLaunchRequest request,
        CancellationToken cancellationToken
    )
    {
        var completion = new TaskCompletionSource<SingleInstanceActivationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                completion.SetResult(
                    await HandleSingleInstanceActivationOnUiThreadAsync(request, cancellationToken)
                );
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        return completion.Task;
    }

    private async Task<SingleInstanceActivationResult> HandleSingleInstanceActivationOnUiThreadAsync(
        SingleInstanceLaunchRequest request,
        CancellationToken cancellationToken
    )
    {
        if (ApplicationVersionComparer.IsNewer(request.AppVersion, ApplicationVersion.Current))
        {
            var upgradeAccepted = await PrepareUpgradeExitAsync(cancellationToken);

            if (upgradeAccepted)
            {
                return SingleInstanceActivationResult.UpgradeAccepted;
            }

            ShowAndActivateMainWindow();
            return SingleInstanceActivationResult.UpgradeRejected;
        }

        ShowAndActivateMainWindow();
        return SingleInstanceActivationResult.ActivatedExisting;
    }

    private async Task<bool> PrepareUpgradeExitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _upgradeShutdownStarted, 1) != 0)
        {
            return true;
        }

        if (_lifetime is null)
        {
            return true;
        }

        var finalized = await _lifetime.FinalizeRecordingForUpgradeAsync(cancellationToken);

        if (!finalized)
        {
            Interlocked.Exchange(ref _upgradeShutdownStarted, 0);
            MessageBox.Show(
                "PullWatch could not finish the active recording. The current version will remain running.",
                "PullWatch upgrade could not continue",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        return finalized;
    }

    private void CompleteSingleInstanceActivationExchange(
        SingleInstanceActivationResult result,
        bool responseSent
    )
    {
        if (result != SingleInstanceActivationResult.UpgradeAccepted)
        {
            return;
        }

        if (!responseSent)
        {
            Interlocked.Exchange(ref _upgradeShutdownStarted, 0);
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _lifetime?.BeginForcedExit();
            Shutdown();
        });
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
