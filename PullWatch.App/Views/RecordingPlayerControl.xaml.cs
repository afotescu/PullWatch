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
    private const double FallbackUnmuteVolume = 0.5;
    private const int KeyboardSeekCommitDelayMilliseconds = 150;
    private static readonly TimeSpan EndSeekInset = TimeSpan.FromMilliseconds(50);

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

    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _keyboardSeekTimer;
    private bool _hasMedia;
    private bool _hasPlaybackEnded;
    private bool _isPlaying;
    private bool _isSeeking;
    private int _surfacePressClickCount;
    private bool _isMuted;
    private bool _isAdjustingVolume;
    private bool _isUpdatingVolumeControls;
    private double _lastAudibleVolume;
    private double _volume = FallbackUnmuteVolume;
    private int _sourceLoadVersion;
    private TimeSpan? _pendingPlaybackStartPosition;
    private TimeSpan? _pendingKeyboardSeekPosition;
    private bool _pendingKeyboardSeekNeedsCommit;
    private string? _playbackErrorText;
    private bool _isDisposed;

    public RecordingPlayerControl()
    {
        InitializeComponent();
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
        _lastAudibleVolume = _volume;
        ApplyPlayerAudioState();
        UpdateVolumeControls();
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

    internal void ApplyPlaybackAudioState(int volumePercent, bool isMuted)
    {
        _isAdjustingVolume = false;
        _volume = Math.Clamp(volumePercent, 0, 100) / VolumeSliderScale;

        if (_volume > 0)
        {
            _lastAudibleVolume = _volume;
        }
        else if (_lastAudibleVolume <= 0)
        {
            _lastAudibleVolume = FallbackUnmuteVolume;
        }

        _isMuted = isMuted || _volume <= 0;
        ApplyPlayerAudioState();
        UpdateVolumeControls();
    }

    public void StopPlayback()
    {
        _sourceLoadVersion++;
        StopPlaybackCore();
        MediaPlayer.Source = null;
        ResetPlayerState(sourceAvailable: false);
    }

    public void SuspendPlayback()
    {
        CancelPendingSurfaceInteraction();
        PausePlayback();
    }

    public void DisposePlayback()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Loaded -= OnLoaded;
        StopPlayback();
        MediaPlayer.Close();
    }

    public bool TogglePlayback()
    {
        if (MediaPlayer.Source is null || !_hasMedia)
        {
            return false;
        }

        if (_isPlaying)
        {
            PausePlayback();
        }
        else
        {
            StartPlayback();
        }

        return true;
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
        return SeekTo((_pendingPlaybackStartPosition ?? MediaPlayer.Position) + offset);
    }

    public bool AdjustVolume(double delta)
    {
        SetVolume(_volume + delta, unmute: true);
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
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (!_isDisposed && Source is not null && !Equals(MediaPlayer.Source, Source))
        {
            ScheduleLoadSource(Source);
        }
    }

    private void ScheduleLoadSource(Uri? source)
    {
        if (_isDisposed)
        {
            return;
        }

        CancelPendingSurfaceInteraction();
        var loadVersion = ++_sourceLoadVersion;

        if (source is null)
        {
            LoadSource(null);
            return;
        }

        if (IsReadyToLoadSource())
        {
            LoadSource(source);
            return;
        }

        Dispatcher.InvokeAsync(
            () =>
            {
                if (loadVersion != _sourceLoadVersion || _isDisposed || !IsReadyToLoadSource())
                {
                    return;
                }

                LoadSource(source);
            },
            DispatcherPriority.Loaded
        );
    }

    private bool IsReadyToLoadSource()
    {
        return IsLoaded && PresentationSource.FromVisual(MediaPlayer) is not null;
    }

    private void LoadSource(Uri? source)
    {
        StopPlaybackCore();
        MediaPlayer.Source = null;
        ResetPlayerState(source is not null);

        if (source is null)
        {
            return;
        }

        MediaPlayer.IsMuted = true;
        MediaPlayer.Source = source;
        MediaPlayer.Play();
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

    private void OnPlayerMediaOpened(object sender, RoutedEventArgs eventArgs)
    {
        if (MediaPlayer.Source is null)
        {
            return;
        }

        _hasMedia = true;
        _hasPlaybackEnded = false;
        _pendingPlaybackStartPosition = null;
        _isPlaying = false;
        MediaPlayer.Pause();
        MediaPlayer.Position = TimeSpan.Zero;
        ApplyPlayerAudioState();

        PlayPauseButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        UpdatePlayPauseButton();
        UpdateDurationFromPlayer();
        UpdatePositionFromPlayer();
        PlayerPreviewCover.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
    }

    private void OnPlayerMediaEnded(object sender, RoutedEventArgs eventArgs)
    {
        _positionTimer.Stop();
        _isPlaying = false;
        _hasPlaybackEnded = true;
        _pendingPlaybackStartPosition = null;
        UpdatePlayPauseButton();
        PlaybackSlider.Value = PlaybackSlider.Maximum;
        UpdatePlaybackTimeText(GetDuration(), GetDuration());
    }

    private void OnPlayerMediaFailed(object sender, ExceptionRoutedEventArgs eventArgs)
    {
        ShowPlaybackError(
            $"This recording could not be played: {eventArgs.ErrorException.Message}"
        );
    }

    private void ShowPlaybackError(string message)
    {
        StopPlaybackCore();
        _hasMedia = false;
        _playbackErrorText = message;
        PlayPauseButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        PlaybackSlider.IsEnabled = false;
        UpdatePlaceholderText();
        PlayerPreviewCover.SetCurrentValue(VisibilityProperty, Visibility.Visible);
    }

    private void OnPositionTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!_isSeeking && _pendingKeyboardSeekPosition is null)
        {
            UpdatePositionFromPlayer();
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
            Key.End => SeekTo(GetDuration()),
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
            UpdatePlaybackTimeText(TimeSpan.FromSeconds(PlaybackSlider.Value), GetDuration());
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

        SetVolume(eventArgs.NewValue / VolumeSliderScale, unmute: eventArgs.NewValue > 0);

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
        if (IsEffectivelyMuted())
        {
            _volume = _lastAudibleVolume;
            _isMuted = false;
        }
        else
        {
            _lastAudibleVolume = _volume;
            _isMuted = true;
        }

        ApplyPlayerAudioState();
        UpdateVolumeControls();
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

        if (!_hasMedia)
        {
            return false;
        }

        var duration = GetDuration();
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var position = Clamp(requestedPosition, TimeSpan.Zero, duration);
        if (_hasPlaybackEnded)
        {
            _pendingPlaybackStartPosition = position >= duration ? TimeSpan.Zero : position;
        }
        else
        {
            MediaPlayer.Position =
                position >= duration
                    ? Clamp(duration - EndSeekInset, TimeSpan.Zero, duration)
                    : position;
        }

        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, duration);
        return true;
    }

    private bool SeekByFromKeyboard(TimeSpan offset, bool isRepeat)
    {
        if (!_hasMedia)
        {
            return false;
        }

        var duration = GetDuration();
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var origin =
            _pendingKeyboardSeekPosition ?? _pendingPlaybackStartPosition ?? MediaPlayer.Position;
        var position = Clamp(origin + offset, TimeSpan.Zero, duration);

        if (isRepeat)
        {
            PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
            UpdatePlaybackTimeText(position, duration);
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

    private void SetVolume(double volume, bool unmute)
    {
        var clampedVolume = Math.Clamp(volume, 0, 1);
        _volume = clampedVolume;

        if (clampedVolume > 0)
        {
            _lastAudibleVolume = clampedVolume;
        }

        if (unmute && clampedVolume > 0)
        {
            _isMuted = false;
        }
        else if (clampedVolume <= 0)
        {
            _isMuted = true;
        }

        ApplyPlayerAudioState();
        UpdateVolumeControls();
    }

    private void RaisePlaybackAudioStateChanged()
    {
        PlaybackAudioStateChanged?.Invoke(
            this,
            new PlaybackAudioStateChangedEventArgs(
                (int)Math.Round(_volume * VolumeSliderScale),
                IsEffectivelyMuted()
            )
        );
    }

    private void SeekToPoint(System.Windows.Point point)
    {
        if (!_hasMedia || PlaybackSlider.ActualWidth <= 0)
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

    private void UpdatePositionFromPlayer()
    {
        var position = MediaPlayer.Position;
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, GetDuration());
    }

    private void StopPlaybackCore()
    {
        CancelPendingSurfaceInteraction();
        CancelPendingKeyboardSeek();
        _hasPlaybackEnded = false;
        _pendingPlaybackStartPosition = null;
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayPauseButton();

        if (MediaPlayer.Source is not null)
        {
            MediaPlayer.Stop();
        }

        ApplyPlayerAudioState();
    }

    private void StartPlayback()
    {
        var duration = GetDuration();
        if (_hasPlaybackEnded || (duration > TimeSpan.Zero && MediaPlayer.Position >= duration))
        {
            var startPosition = Clamp(
                _pendingPlaybackStartPosition ?? TimeSpan.Zero,
                TimeSpan.Zero,
                duration
            );
            MediaPlayer.Position = startPosition;
            PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, startPosition.TotalSeconds);
            UpdatePlaybackTimeText(startPosition, duration);
        }

        _hasPlaybackEnded = false;
        _pendingPlaybackStartPosition = null;
        ApplyPlayerAudioState();
        MediaPlayer.Play();
        _isPlaying = true;
        UpdatePlayPauseButton();
        _positionTimer.Start();
    }

    private void PausePlayback()
    {
        if (MediaPlayer.Source is not null && _hasMedia)
        {
            MediaPlayer.Pause();
        }

        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayPauseButton();
    }

    private void UpdatePlayPauseButton()
    {
        if (_isPlaying)
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

    private void UpdateVolumeControls()
    {
        _isUpdatingVolumeControls = true;
        VolumeSlider.Value = Math.Round(_volume * VolumeSliderScale);
        VolumeSlider.ToolTip = $"{VolumeSlider.Value:0}% volume";
        _isUpdatingVolumeControls = false;

        if (IsEffectivelyMuted())
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
            _playbackErrorText ?? PlaceholderText
        );
    }

    private void UpdatePlaybackTimeText(TimeSpan position, TimeSpan duration)
    {
        PlaybackTimeText.Text =
            $"{RecordingTimeFormatter.FormatPlaybackTime(position)} / {RecordingTimeFormatter.FormatPlaybackTime(duration)}";
    }

    private void ResetPlayerState(bool sourceAvailable)
    {
        CancelPendingSurfaceInteraction();
        CancelPendingKeyboardSeek();
        _hasMedia = false;
        _hasPlaybackEnded = false;
        _pendingPlaybackStartPosition = null;
        _isSeeking = false;
        _playbackErrorText = null;
        PlayPauseButton.IsEnabled = false;
        FullScreenButton.IsEnabled = sourceAvailable;
        PlaybackSlider.IsEnabled = false;
        PlaybackSlider.Maximum = 0;
        PlaybackSlider.Value = 0;
        UpdatePlaybackTimeText(TimeSpan.Zero, TimeSpan.Zero);
        UpdatePlaceholderText();
        PlayerPreviewCover.SetCurrentValue(VisibilityProperty, Visibility.Visible);
    }

    private void UpdateDurationFromPlayer()
    {
        var duration = GetDuration();
        PlaybackSlider.Maximum = duration.TotalSeconds;
        PlaybackSlider.IsEnabled = _hasMedia && duration > TimeSpan.Zero;
    }

    private void ApplyPlayerAudioState()
    {
        MediaPlayer.Volume = _volume;
        MediaPlayer.IsMuted = IsEffectivelyMuted();
    }

    private TimeSpan GetDuration()
    {
        return MediaPlayer.NaturalDuration.HasTimeSpan
            ? MediaPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
    }

    private bool IsEffectivelyMuted()
    {
        return _isMuted || _volume <= 0;
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
