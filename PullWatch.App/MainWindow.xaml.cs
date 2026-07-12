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
    private readonly Dictionary<object, FrameworkElement> _navigationViews = new(
        ReferenceEqualityComparer.Instance
    );
    private Action? _fullScreenExitRequested;
    private Rect _windowBoundsBeforeFullScreen;
    private ResizeMode _resizeModeBeforeFullScreen;
    private WindowState _windowStateBeforeFullScreen;
    private WindowStyle _windowStyleBeforeFullScreen;
    private bool _placementSaved;
    private bool _navigationViewsDisposed;

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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowSelectedNavigationContent();
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

        if (_controller.Status.VideoEncoding?.IsValid == true)
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

        var environment = await FfmpegToolPaths.ResolveEnvironmentAsync(cancellationToken);
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

            if (!result.SaveResult.WasPersisted)
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
        RequestFullScreenExit();

        if (!_lifetime.ShouldHideOnWindowClose)
        {
            DisposeNavigationViews();
            _viewModel.DiscardPendingSettingsDraftsForExit();
            SaveWindowPlacement();
            return;
        }

        eventArgs.Cancel = true;
        SuspendNavigationPlayback();
        Hide();
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        _viewModel.Recordings.UpdateDuration();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        DisposeNavigationViews();
        SaveWindowPlacement();
        _durationTimer.Stop();
        _viewModel.Dispose();
    }

    internal bool EnterFullScreenPlayer(RecordingPlayerControl player, Action exitRequested)
    {
        if (_fullScreenExitRequested is not null || FullScreenPlayerHost.Content is not null)
        {
            return false;
        }

        _windowBoundsBeforeFullScreen =
            WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _resizeModeBeforeFullScreen = ResizeMode;
        _windowStateBeforeFullScreen = WindowState;
        _windowStyleBeforeFullScreen = WindowStyle;
        _fullScreenExitRequested = exitRequested;

        FullScreenLayer.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        FullScreenPlayerHost.Content = player;
        player.IsFullScreen = true;
        Activate();
        return true;
    }

    internal bool ExitFullScreenPlayer(RecordingPlayerControl player)
    {
        if (!ReferenceEquals(FullScreenPlayerHost.Content, player))
        {
            return false;
        }

        FullScreenPlayerHost.Content = null;
        player.IsFullScreen = false;
        _fullScreenExitRequested = null;
        RestoreWindowAfterFullScreen();
        FullScreenLayer.Visibility = Visibility.Collapsed;
        return true;
    }

    private void RequestFullScreenExit()
    {
        _fullScreenExitRequested?.Invoke();
    }

    private void RestoreWindowAfterFullScreen()
    {
        WindowState = WindowState.Normal;
        WindowStyle = _windowStyleBeforeFullScreen;
        ResizeMode = _resizeModeBeforeFullScreen;
        Left = _windowBoundsBeforeFullScreen.Left;
        Top = _windowBoundsBeforeFullScreen.Top;
        Width = _windowBoundsBeforeFullScreen.Width;
        Height = _windowBoundsBeforeFullScreen.Height;

        if (_windowStateBeforeFullScreen == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.SelectedNavigationItem))
        {
            ShowSelectedNavigationContent();
        }
    }

    private void ShowSelectedNavigationContent()
    {
        var content = _viewModel.SelectedNavigationItem.Content;
        if (!_navigationViews.TryGetValue(content, out var view))
        {
            view = CreateNavigationView(content);
            _navigationViews.Add(content, view);
        }

        NavigationContent.Content = view;
    }

    private FrameworkElement CreateNavigationView(object content)
    {
        var template = TryFindResource(new DataTemplateKey(content.GetType())) as DataTemplate;
        if (template?.LoadContent() is not FrameworkElement view)
        {
            throw new InvalidOperationException(
                $"No navigation view template is registered for {content.GetType().FullName}."
            );
        }

        view.DataContext = content;
        return view;
    }

    private void DisposeNavigationViews()
    {
        if (_navigationViewsDisposed)
        {
            return;
        }

        _navigationViewsDisposed = true;
        RequestFullScreenExit();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        NavigationContent.Content = null;

        foreach (var view in _navigationViews.Values.OfType<IDisposable>())
        {
            view.Dispose();
        }

        _navigationViews.Clear();
    }

    private void SuspendNavigationPlayback()
    {
        foreach (var view in _navigationViews.Values.OfType<RecordingsView>())
        {
            view.SuspendPlayback();
        }
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
