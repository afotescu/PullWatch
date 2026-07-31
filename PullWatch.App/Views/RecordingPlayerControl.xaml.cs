using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PullWatch;

internal sealed class PlaybackAudioStateChangedEventArgs(int volumePercent, bool isMuted)
    : EventArgs
{
    public int VolumePercent { get; } = volumePercent;
    public bool IsMuted { get; } = isMuted;
}

public partial class RecordingPlayerControl : UserControl
{
    private const string PlayIconGeometryKey = "PlayIconGeometry";
    private const string StopIconGeometryKey = "StopIconGeometry";
    private const string EnterFullScreenIconGeometryKey = "EnterFullScreenIconGeometry";
    private const string ExitFullScreenIconGeometryKey = "ExitFullScreenIconGeometry";
    private const string VolumeIconGeometryKey = "VolumeIconGeometry";
    private const string MutedIconGeometryKey = "MutedIconGeometry";
    private const double SeekStepSeconds = 5;
    private const double VolumeStep = 0.1;
    private const double VolumeSliderScale = 100;
    private const int KeyboardSeekCommitDelayMilliseconds = 150;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(Uri),
        typeof(RecordingPlayerControl),
        new PropertyMetadata(null, OnSourceChanged)
    );

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(RecordingPlayerControl),
        new PropertyMetadata(string.Empty, OnPlaceholderTextChanged)
    );

    public static readonly DependencyProperty IsFullScreenProperty = DependencyProperty.Register(
        nameof(IsFullScreen),
        typeof(bool),
        typeof(RecordingPlayerControl),
        new PropertyMetadata(false, OnIsFullScreenChanged)
    );

    public static readonly DependencyProperty NotificationsProperty = DependencyProperty.Register(
        nameof(Notifications),
        typeof(NotificationCenterViewModel),
        typeof(RecordingPlayerControl),
        new PropertyMetadata(null, OnNotificationsChanged)
    );

    private readonly IPlaybackSession _session;
    private readonly PlaybackCoordinator _coordinator;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _keyboardSeekTimer;
    private bool _isSeeking;
    private int _surfacePressClickCount;
    private bool _isAdjustingVolume;
    private bool _isUpdatingVolumeControls;
    private TimeSpan? _pendingKeyboardSeekPosition;
    private bool _pendingKeyboardSeekNeedsCommit;
    private bool _isDisposed;

    public RecordingPlayerControl()
    {
        var app = (App)Application.Current;
        var logger = app.CreateLogger<RecordingPlayerControl>();
        InitializeComponent();

        var uiDispatcher = new WpfUiDispatcher(Dispatcher);
        _session = app.CreatePlaybackSession(uiDispatcher, logger);
        MediaPlayer.Attach(_session);
        _coordinator = new PlaybackCoordinator(_session, uiDispatcher, logger);
        _coordinator.StateChanged += OnPlaybackStateChanged;

        PlayerPlaceholder.SetCurrentValue(TextBlock.TextProperty, PlaceholderText);
        _positionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnPositionTimerTick,
            Dispatcher
        );
        _keyboardSeekTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(KeyboardSeekCommitDelayMilliseconds),
            DispatcherPriority.Input,
            OnKeyboardSeekTimerTick,
            Dispatcher
        );
        _keyboardSeekTimer.Stop();
        PlaybackSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(OnPlaybackThumbDragStarted)
        );
        PlaybackSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnPlaybackThumbDragCompleted)
        );
        VolumeSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(OnVolumeThumbDragStarted)
        );
        VolumeSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnVolumeThumbDragCompleted)
        );
        RenderPlaybackState();
        Loaded += OnLoaded;
    }

    public event EventHandler? FullScreenRequested;

    public event EventHandler? ExitFullScreenRequested;

    internal event EventHandler<PlaybackAudioStateChangedEventArgs>? PlaybackAudioStateChanged;

    public Uri? Source
    {
        get => (Uri?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool IsFullScreen
    {
        get => (bool)GetValue(IsFullScreenProperty);
        set => SetValue(IsFullScreenProperty, value);
    }

    public NotificationCenterViewModel? Notifications
    {
        get => (NotificationCenterViewModel?)GetValue(NotificationsProperty);
        set => SetValue(NotificationsProperty, value);
    }

    internal void ApplyPlaybackAudioState(int volumePercent, bool isMuted)
    {
        _isAdjustingVolume = false;
        _coordinator.ApplyAudioState(volumePercent, isMuted);
    }

    public void StopPlayback()
    {
        CancelPendingSurfaceInteraction();
        CancelPendingKeyboardSeek();
        _coordinator.Stop();
    }

    public void SuspendPlayback()
    {
        CancelPendingSurfaceInteraction();
        _coordinator.Pause();
    }

    public void DisposePlayback()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Loaded -= OnLoaded;
        _positionTimer.Stop();
        _keyboardSeekTimer.Stop();
        _coordinator.StateChanged -= OnPlaybackStateChanged;
        _coordinator.Dispose();
        MediaPlayer.DetachSession();
        _session.Dispose();
        MediaPlayer.Dispose();
    }

    public bool TogglePlayback()
    {
        return _coordinator.TogglePlayback();
    }

    public bool HandlePlaybackKey(Key key, bool isRepeat = false)
    {
        return key switch
        {
            Key.Space => TogglePlayback(),
            Key.Left => SeekByFromKeyboard(TimeSpan.FromSeconds(-SeekStepSeconds), isRepeat),
            Key.Right => SeekByFromKeyboard(TimeSpan.FromSeconds(SeekStepSeconds), isRepeat),
            Key.Up => AdjustVolume(VolumeStep),
            Key.Down => AdjustVolume(-VolumeStep),
            Key.M => ToggleMuteFromKeyboard(),
            Key.F or Key.F11 => ToggleFullScreenFromKeyboard(),
            _ => false,
        };
    }

    public bool SeekBy(TimeSpan offset)
    {
        CancelPendingKeyboardSeek();
        return _coordinator.SeekBy(offset);
    }

    public bool AdjustVolume(double delta)
    {
        var volume = _coordinator.State.VolumePercent + delta * VolumeSliderScale;
        _coordinator.SetVolumePercent((int)Math.Round(volume), unmute: true);
        RaisePlaybackAudioStateChanged();
        return true;
    }

    private static void OnSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs
    )
    {
        var player = (RecordingPlayerControl)dependencyObject;
        player.ScheduleLoadSource((Uri?)eventArgs.NewValue);
    }

    private static void OnPlaceholderTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs
    )
    {
        var player = (RecordingPlayerControl)dependencyObject;
        player.UpdatePlaceholderText();
    }

    private static void OnIsFullScreenChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs
    )
    {
        var player = (RecordingPlayerControl)dependencyObject;
        player.UpdateFullScreenButton();
        player.UpdateNotificationOverlayVisibility();
    }

    private static void OnNotificationsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs
    )
    {
        var player = (RecordingPlayerControl)dependencyObject;
        player.PlayerNotifications.DataContext = eventArgs.NewValue;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_isDisposed || Source is null)
        {
            return;
        }

        if (TryStartPendingOpen())
        {
            return;
        }

        ScheduleLoadSource(Source);
    }

    private void ScheduleLoadSource(Uri? source)
    {
        if (_isDisposed)
        {
            return;
        }

        CancelPendingSurfaceInteraction();
        CancelPendingKeyboardSeek();
        _coordinator.RequestSource(source);
        if (source is not null)
        {
            StartOrSchedulePendingOpen();
        }
    }

    private void StartOrSchedulePendingOpen()
    {
        if (TryStartPendingOpen())
        {
            return;
        }

        Dispatcher.InvokeAsync(
            () =>
            {
                if (!_isDisposed)
                {
                    TryStartPendingOpen();
                }
            },
            DispatcherPriority.Loaded
        );
    }

    private bool TryStartPendingOpen()
    {
        return IsReadyToLoadSource() && _coordinator.StartPendingOpen();
    }

    private bool IsReadyToLoadSource()
    {
        return IsLoaded && PresentationSource.FromVisual(MediaPlayer) is not null;
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs eventArgs)
    {
        TogglePlayback();
    }

    private void OnFullScreenClicked(object sender, RoutedEventArgs eventArgs)
    {
        RequestFullScreenToggle();
    }

    private void OnVideoSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Focus();
        _surfacePressClickCount = eventArgs.ClickCount;
    }

    private void OnVideoSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var clickCount = _surfacePressClickCount;
        _surfacePressClickCount = 0;
        if (clickCount == 0)
        {
            return;
        }

        var handled = PlayPauseButton.IsEnabled && TogglePlayback();
        if (clickCount == 2 && FullScreenButton.IsEnabled)
        {
            handled = true;
            RequestFullScreenToggle();
        }

        eventArgs.Handled = handled;
    }

    private void OnVideoSurfaceMouseLeave(object sender, MouseEventArgs eventArgs)
    {
        CancelPendingSurfaceInteraction();
    }

    private void RequestFullScreenToggle()
    {
        if (IsFullScreen)
        {
            ExitFullScreenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        FullScreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMuteClicked(object sender, RoutedEventArgs eventArgs)
    {
        ToggleMute();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs eventArgs)
    {
        RenderPlaybackState();
    }

    private void OnPositionTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!_isSeeking && _pendingKeyboardSeekPosition is null)
        {
            _coordinator.RefreshPosition();
        }
    }

    private void OnKeyboardSeekTimerTick(object? sender, EventArgs eventArgs)
    {
        _keyboardSeekTimer.Stop();
        var position = _pendingKeyboardSeekPosition;
        var needsCommit = _pendingKeyboardSeekNeedsCommit;
        _pendingKeyboardSeekPosition = null;
        _pendingKeyboardSeekNeedsCommit = false;
        if (needsCommit && position is not null)
        {
            SeekTo(position.Value);
        }

        RenderPlaybackState();
    }

    private void OnPlaybackSliderPreviewMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!IsFromThumb(eventArgs.OriginalSource as DependencyObject))
        {
            SeekToPoint(eventArgs.GetPosition(PlaybackSlider));
            eventArgs.Handled = true;
            return;
        }

        _isSeeking = true;
    }

    private void OnPlaybackSliderPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        eventArgs.Handled = eventArgs.Key switch
        {
            Key.Left or Key.Down => SeekByFromKeyboard(
                TimeSpan.FromSeconds(-SeekStepSeconds),
                eventArgs.IsRepeat
            ),
            Key.Right or Key.Up => SeekByFromKeyboard(
                TimeSpan.FromSeconds(SeekStepSeconds),
                eventArgs.IsRepeat
            ),
            Key.PageDown => SeekByFromKeyboard(
                TimeSpan.FromSeconds(-SeekStepSeconds * 2),
                eventArgs.IsRepeat
            ),
            Key.PageUp => SeekByFromKeyboard(
                TimeSpan.FromSeconds(SeekStepSeconds * 2),
                eventArgs.IsRepeat
            ),
            Key.Home => SeekTo(TimeSpan.Zero),
            Key.End => SeekTo(_coordinator.State.Duration),
            _ => false,
        };
    }

    private void OnPlaybackThumbDragStarted(object sender, DragStartedEventArgs eventArgs)
    {
        _isSeeking = true;
    }

    private void OnPlaybackThumbDragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        SeekToSliderValue();
        _isSeeking = false;
    }

    private void OnPlaybackSliderValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs
    )
    {
        if (_isSeeking)
        {
            UpdatePlaybackTimeText(
                TimeSpan.FromSeconds(PlaybackSlider.Value),
                _coordinator.State.Duration
            );
        }
    }

    private void OnVolumeThumbDragStarted(object sender, DragStartedEventArgs eventArgs)
    {
        _isAdjustingVolume = true;
    }

    private void OnVolumeThumbDragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        _isAdjustingVolume = false;
        RaisePlaybackAudioStateChanged();
    }

    private void OnVolumeSliderValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs
    )
    {
        if (_isUpdatingVolumeControls)
        {
            return;
        }

        _coordinator.SetVolumePercent(
            (int)Math.Round(eventArgs.NewValue),
            unmute: eventArgs.NewValue > 0
        );
        if (!_isAdjustingVolume)
        {
            RaisePlaybackAudioStateChanged();
        }
    }

    private void SeekToSliderValue()
    {
        SeekTo(TimeSpan.FromSeconds(PlaybackSlider.Value));
    }

    private void ToggleMute()
    {
        _coordinator.ToggleMute();
        RaisePlaybackAudioStateChanged();
    }

    private bool ToggleMuteFromKeyboard()
    {
        ToggleMute();
        return true;
    }

    private bool ToggleFullScreenFromKeyboard()
    {
        if (!FullScreenButton.IsEnabled)
        {
            return false;
        }

        RequestFullScreenToggle();
        return true;
    }

    private bool SeekTo(TimeSpan requestedPosition)
    {
        CancelPendingKeyboardSeek();
        return _coordinator.SeekTo(requestedPosition);
    }

    private bool SeekByFromKeyboard(TimeSpan offset, bool isRepeat)
    {
        var state = _coordinator.State;
        if (!state.HasMedia || state.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        var origin = _pendingKeyboardSeekPosition ?? state.Position;
        var position = Clamp(origin + offset, TimeSpan.Zero, state.Duration);
        if (isRepeat)
        {
            PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
            UpdatePlaybackTimeText(position, state.Duration);
        }
        else
        {
            SeekTo(position);
        }

        _pendingKeyboardSeekPosition = position;
        _pendingKeyboardSeekNeedsCommit = isRepeat;
        _keyboardSeekTimer.Stop();
        _keyboardSeekTimer.Start();
        return true;
    }

    private void RaisePlaybackAudioStateChanged()
    {
        var state = _coordinator.State;
        PlaybackAudioStateChanged?.Invoke(
            this,
            new PlaybackAudioStateChangedEventArgs(
                state.VolumePercent,
                state.IsMuted || state.VolumePercent <= 0
            )
        );
    }

    private void SeekToPoint(System.Windows.Point point)
    {
        if (!_coordinator.State.HasMedia || PlaybackSlider.ActualWidth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(point.X / PlaybackSlider.ActualWidth, 0, 1);
        PlaybackSlider.Value =
            PlaybackSlider.Minimum + ratio * (PlaybackSlider.Maximum - PlaybackSlider.Minimum);
        SeekToSliderValue();
    }

    private static bool IsFromThumb(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void RenderPlaybackState()
    {
        var state = _coordinator.State;
        if (state.IsPlaying)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }

        PlayPauseButton.IsEnabled = state.HasMedia;
        FullScreenButton.IsEnabled = state.IsOpening || state.HasMedia;
        PlaybackSlider.IsEnabled = state.HasMedia && state.Duration > TimeSpan.Zero;
        PlaybackSlider.Maximum = Math.Max(0, state.Duration.TotalSeconds);
        if (!_isSeeking && _pendingKeyboardSeekPosition is null)
        {
            PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, state.Position.TotalSeconds);
            UpdatePlaybackTimeText(state.Position, state.Duration);
        }

        UpdatePlayPauseButton();
        UpdateVolumeControls();
        UpdatePlaceholderText();
        PlayerPreviewCover.SetCurrentValue(
            VisibilityProperty,
            state.IsOpen ? Visibility.Collapsed : Visibility.Visible
        );
    }

    private void UpdatePlayPauseButton()
    {
        if (_coordinator.State.IsPlaying)
        {
            PlayPauseIcon.Data = (Geometry)FindResource(StopIconGeometryKey);
            PlayPauseButton.ToolTip = "Pause";
            return;
        }

        PlayPauseIcon.Data = (Geometry)FindResource(PlayIconGeometryKey);
        PlayPauseButton.ToolTip = "Play";
    }

    private void UpdateFullScreenButton()
    {
        if (IsFullScreen)
        {
            FullScreenIcon.Data = (Geometry)FindResource(ExitFullScreenIconGeometryKey);
            FullScreenButton.ToolTip = "Exit fullscreen";
            return;
        }

        FullScreenIcon.Data = (Geometry)FindResource(EnterFullScreenIconGeometryKey);
        FullScreenButton.ToolTip = "Enter fullscreen";
    }

    private void UpdateNotificationOverlayVisibility()
    {
        PlayerNotifications.SetCurrentValue(
            VisibilityProperty,
            IsFullScreen ? Visibility.Collapsed : Visibility.Visible
        );
    }

    private void UpdateVolumeControls()
    {
        var state = _coordinator.State;
        _isUpdatingVolumeControls = true;
        VolumeSlider.Value = state.VolumePercent;
        VolumeSlider.ToolTip = $"{VolumeSlider.Value:0}% volume";
        _isUpdatingVolumeControls = false;

        if (state.IsMuted || state.VolumePercent <= 0)
        {
            MuteIcon.Data = (Geometry)FindResource(MutedIconGeometryKey);
            MuteButton.ToolTip = "Unmute";
            return;
        }

        MuteIcon.Data = (Geometry)FindResource(VolumeIconGeometryKey);
        MuteButton.ToolTip = "Mute";
    }

    private void UpdatePlaceholderText()
    {
        PlayerPlaceholder.SetCurrentValue(
            TextBlock.TextProperty,
            _coordinator.State.ErrorText ?? PlaceholderText
        );
    }

    private void UpdatePlaybackTimeText(TimeSpan position, TimeSpan duration)
    {
        PlaybackTimeText.Text =
            $"{RecordingTimeFormatter.FormatPlaybackTime(position)} / {RecordingTimeFormatter.FormatPlaybackTime(duration)}";
    }

    private void CancelPendingSurfaceInteraction()
    {
        _surfacePressClickCount = 0;
    }

    private void CancelPendingKeyboardSeek()
    {
        _keyboardSeekTimer.Stop();
        _pendingKeyboardSeekPosition = null;
        _pendingKeyboardSeekNeedsCommit = false;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        return value > maximum ? maximum : value;
    }
}
