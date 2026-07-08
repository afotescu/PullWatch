using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public partial class MainWindow : Window
{
    private readonly ApplicationController _controller;
    private readonly MainWindowViewModel _viewModel;
    private readonly FfmpegEncoderTestService _encoderTestService;
    private readonly DispatcherTimer _durationTimer;
    private readonly ApplicationLifetimeCoordinator _lifetime;
    private bool _placementSaved;

    internal MainWindow(
        ApplicationController controller,
        ApplicationLifetimeCoordinator lifetime,
        InMemoryLogProvider logs,
        ILogger<FfmpegEncoderTestService> encoderTestLogger,
        IWindowsStartupShortcut windowsStartupShortcut,
        IApplicationUpdater applicationUpdater,
        bool showSettingsOnStartup
    )
    {
        InitializeComponent();
        _controller = controller;
        _lifetime = lifetime;
        _encoderTestService = new FfmpegEncoderTestService(
            GetWindowHandleForEncoderTest,
            encoderTestLogger
        );
        _viewModel = new MainWindowViewModel(
            controller,
            new WpfUiDispatcher(Dispatcher),
            new WpfSettingsDialogs(),
            logs,
            new WpfDiagnosticsDialogs(),
            new WpfRecordingDialogs(),
            TestVideoEncodingFromSettingsAsync,
            windowsStartupShortcut,
            applicationUpdater,
            RequestShutdownForUpdate,
            showSettingsOnStartup
        );
        DataContext = _viewModel;
        RestoreWindowPlacement(controller.Status.EffectiveSettings?.Ui.WindowPlacement);
        _durationTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnTimerTick,
            Dispatcher
        );
        Closed += OnClosed;
        Closing += OnClosing;
        _viewModel.StartAutomaticUpdateCheck();
    }

    internal async Task PromptForEncoderCalibrationIfNeededAsync(
        CancellationToken cancellationToken
    )
    {
        var settings = _controller.Status.EffectiveSettings;
        if (settings is null)
        {
            return;
        }

        var environment = await FfmpegToolPaths.ResolveEnvironmentAsync(cancellationToken);
        var status = EncoderCalibrationStatusEvaluator.Evaluate(settings, environment);
        if (status.IsValid)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        var promptResult = WpfConfirmationDialog.Show(
            this,
            new ConfirmationDialogRequest(
                "PullWatch",
                [
                    "Recording stays disabled until this test passes. It only takes a few seconds and lets PullWatch choose the best working encoder.",
                ],
                [
                    new ConfirmationDialogButton(
                        "Test video encoding",
                        ConfirmationDialogResult.Primary,
                        ConfirmationDialogButtonKind.Accent,
                        IsDefault: true
                    ),
                    new ConfirmationDialogButton(
                        "Not now",
                        ConfirmationDialogResult.Cancel,
                        IsCancel: true
                    ),
                ],
                Heading: "Test video encoding before recording?"
            )
        );

        if (promptResult != ConfirmationDialogResult.Primary)
        {
            return;
        }

        await RunEncoderCalibrationAsync(environment, cancellationToken);
    }

    private async Task TestVideoEncodingFromSettingsAsync()
    {
        var environment = await FfmpegToolPaths.ResolveEnvironmentAsync(CancellationToken.None);
        await RunEncoderCalibrationAsync(environment, CancellationToken.None);
    }

    private async Task RunEncoderCalibrationAsync(
        EncoderCalibrationEnvironment environment,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var progressWindow = new EncoderCalibrationProgressWindow();
            var result = await progressWindow.RunAsync(
                this,
                progress =>
                    RunEncoderCalibrationOperationAsync(environment, progress, cancellationToken)
            );

            if (!result.SaveResult.IsSaved)
            {
                ShowEncoderCalibrationError(
                    result.SaveResult.ValidationErrors.Count == 0
                        ? "PullWatch could not save the video encoding test results."
                        : string.Join(Environment.NewLine, result.SaveResult.ValidationErrors)
                );
                return;
            }

            if (result.SelectedProfile is null)
            {
                ShowEncoderCalibrationError(
                    "No video encoder profile passed the test. Recording will stay disabled."
                );
                return;
            }

            ShowEncoderCalibrationSuccess(result.SelectedProfile);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowEncoderCalibrationError(exception.Message);
        }
    }

    private async Task<EncoderCalibrationRunResult> RunEncoderCalibrationOperationAsync(
        EncoderCalibrationEnvironment environment,
        IProgress<VideoEncoderTestProgress> progress,
        CancellationToken cancellationToken
    )
    {
        var settings =
            _controller.Status.EffectiveSettings
            ?? throw new InvalidOperationException("Settings are not available.");
        var testResults = await _encoderTestService.TestAsync(
            settings,
            progress,
            cancellationToken
        );
        var calibrationResults = testResults
            .Select(result => result.ToCalibrationResult())
            .ToArray();
        var selectedProfile = VideoProfileSelectionPolicy.SelectBestPassingProfile(
            calibrationResults
        );
        var saveResult = await _controller.SaveSettingsAsync(
            settings with
            {
                Video = settings.Video with { SelectedProfile = selectedProfile },
                EncoderCalibration = new EncoderCalibrationSettings
                {
                    Version = EncoderCalibrationSettings.CurrentVersion,
                    TestedAt = DateTimeOffset.UtcNow,
                    FfmpegPath = environment.FfmpegPath,
                    FfmpegVersion = environment.FfmpegVersion,
                    FfmpegSha256 = environment.FfmpegSha256,
                    Results = calibrationResults,
                },
            },
            cancellationToken
        );

        return new EncoderCalibrationRunResult(saveResult, selectedProfile);
    }

    private sealed record EncoderCalibrationRunResult(
        SettingsSaveResult SaveResult,
        VideoProfileSelection? SelectedProfile
    );

    private void ShowEncoderCalibrationSuccess(VideoProfileSelection selectedProfile)
    {
        WpfConfirmationDialog.Show(
            this,
            new ConfirmationDialogRequest(
                "PullWatch",
                [
                    $"Using {VideoProfileFormatter.FormatDisplayName(selectedProfile)} for future recordings.",
                ],
                [
                    new ConfirmationDialogButton(
                        "OK",
                        ConfirmationDialogResult.Primary,
                        ConfirmationDialogButtonKind.Accent,
                        IsDefault: true
                    ),
                ],
                Heading: "Video encoding is ready"
            )
        );
    }

    private void ShowEncoderCalibrationError(string message)
    {
        WpfConfirmationDialog.Show(
            this,
            new ConfirmationDialogRequest(
                "PullWatch",
                [message],
                [
                    new ConfirmationDialogButton(
                        "OK",
                        ConfirmationDialogResult.Primary,
                        IsDefault: true
                    ),
                ],
                Heading: "Video encoding test failed"
            )
        );
    }

    private nint GetWindowHandleForEncoderTest()
    {
        return new WindowInteropHelper(this).EnsureHandle();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!_lifetime.ShouldHideOnWindowClose)
        {
            _viewModel.DiscardPendingSettingsDraftsForExit();
            SaveWindowPlacement();
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        _viewModel.Recordings.UpdateDuration();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        SaveWindowPlacement();
        _durationTimer.Stop();
        _viewModel.Dispose();
    }

    private void RequestShutdownForUpdate()
    {
        SaveWindowPlacement();
        _lifetime.BeginForcedExit();
        Application.Current.Shutdown();
    }

    private void RestoreWindowPlacement(WindowPlacementSettings? placement)
    {
        if (
            placement?.Left is not { } left
            || placement.Top is not { } top
            || placement.Width is not { } width
            || placement.Height is not { } height
            || width < MinWidth
            || height < MinHeight
        )
        {
            return;
        }

        var bounds = new Rect(left, top, width, height);

        if (!IsVisibleOnCurrentDesktop(bounds))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        if (_placementSaved)
        {
            return;
        }

        _placementSaved = true;

        var restoreBounds =
            WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var placement = new WindowPlacementSettings
        {
            Left = restoreBounds.Left,
            Top = restoreBounds.Top,
            Width = restoreBounds.Width,
            Height = restoreBounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        };
        var currentUi = _controller.Status.EffectiveSettings?.Ui ?? new UiSettings();

        try
        {
            Task.Run(() =>
                    _controller.SaveUiSettingsAsync(currentUi with { WindowPlacement = placement })
                )
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // Shutdown may already be far enough along that settings are no longer available.
        }
    }

    private static bool IsVisibleOnCurrentDesktop(Rect bounds)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight
        );

        return virtualScreen.IntersectsWith(bounds);
    }
}
