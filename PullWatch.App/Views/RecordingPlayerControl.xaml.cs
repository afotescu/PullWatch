using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VlcInstance = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcState = LibVLCSharp.Shared.VLCState;

namespace PullWatch;

public partial class RecordingPlayerControl : UserControl, IDisposable
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
    private const int PreviewSeekPollIntervalMilliseconds = 50;
    private const int PreviewSeekStablePollCount = 3;
    private const int PreviewSeekMaxPollCount = 10;
    private const long PreviewStartToleranceMilliseconds = 100;

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
    private readonly DispatcherTimer _previewSeekTimer;
    private VlcInstance? _libVlc;
    private VlcMediaPlayer? _mediaPlayer;
    private bool _hasAssignedMedia;
    private bool _hasMedia;
    private bool _hasPlaybackEnded;
    private TimeSpan? _pendingPlaybackStartPosition;
    private bool _isPlaying;
    private bool _isPlaybackRequested;
    private bool _isPrimedPaused;
    private volatile bool _isPriming;
    private bool _isPrimingPauseRequested;
    private bool _isPrimingSeekPending;
    private bool _isSeeking;
    private bool _isMuted;
    private bool _isUpdatingVolumeControls;
    private double _lastAudibleVolume;
    private double _volume = FallbackUnmuteVolume;
    private int _playerLifetimeVersion;
    private int _sourceLoadVersion;
    private int _previewSeekPollCount;
    private int _previewSeekStablePollCount;
    private Uri? _assignedSource;
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
        _previewSeekTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(PreviewSeekPollIntervalMilliseconds),
            DispatcherPriority.Background,
            OnPreviewSeekTimerTick,
            Dispatcher
        );
        _previewSeekTimer.Stop();
        PlaybackSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(OnPlaybackThumbDragStarted)
        );
        PlaybackSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnPlaybackThumbDragCompleted)
        );
        _lastAudibleVolume = _volume;
        UpdateVolumeControls();
        Loaded += OnLoaded;
    }

    public event EventHandler? FullScreenRequested;

    public event EventHandler? ExitFullScreenRequested;

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

    public void StopPlayback()
    {
        _sourceLoadVersion++;
        StopPlaybackCore();
        ReleaseMedia();
        ResetPlayerState(sourceAvailable: false);
        FullScreenButton.IsEnabled = false;
    }

    public void SuspendPlayback()
    {
        _positionTimer.Stop();
        _isPlaybackRequested = false;
        _isPlaying = false;
        UpdatePlayPauseButton();

        if (_mediaPlayer is null || !_hasAssignedMedia || _isPriming)
        {
            return;
        }

        _isPrimedPaused = true;
        _mediaPlayer.SetPause(true);
    }

    public void DisposePlayback()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopPlayback();
        DisposePlayer();
        VideoView.Dispose();
    }

    public bool TogglePlayback()
    {
        if (!_hasAssignedMedia || !_hasMedia)
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

    public bool HandlePlaybackKey(Key key)
    {
        return key switch
        {
            Key.Space => TogglePlayback(),
            Key.Left => SeekBy(TimeSpan.FromSeconds(-SeekStepSeconds)),
            Key.Right => SeekBy(TimeSpan.FromSeconds(SeekStepSeconds)),
            Key.Up => AdjustVolume(VolumeStep),
            Key.Down => AdjustVolume(-VolumeStep),
            _ => false,
        };
    }

    public bool SeekBy(TimeSpan offset)
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

        var position = Clamp(
            (_pendingPlaybackStartPosition ?? GetPosition()) + offset,
            TimeSpan.Zero,
            duration
        );
        SetPosition(position);
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, duration);

        return true;
    }

    public bool AdjustVolume(double delta)
    {
        SetVolume(_volume + delta, unmute: true);

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
        if (_isDisposed)
        {
            return;
        }

        if (!EnsurePlayer())
        {
            return;
        }

        if (Source is not null && (!_hasAssignedMedia || !Equals(_assignedSource, Source)))
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
                if (loadVersion != _sourceLoadVersion)
                {
                    return;
                }

                if (!IsReadyToLoadSource())
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
        return IsLoaded && PresentationSource.FromVisual(VideoView) is not null;
    }

    private void LoadSource(Uri? source)
    {
        StopPlaybackCore();
        ReleaseMedia();
        ResetPlayerState(source is not null);

        if (source is null || !EnsurePlayer())
        {
            return;
        }

        try
        {
            _isPriming = true;
            ApplyPlayerAudioState();

            using var media = new VlcMedia(_libVlc!, source);
            _mediaPlayer!.Media = media;
            _hasAssignedMedia = true;
            _assignedSource = source;

            if (!_mediaPlayer.Play())
            {
                throw new InvalidOperationException("VLC rejected the recording.");
            }
        }
        catch (Exception exception)
        {
            ShowPlaybackError($"This recording could not be played: {exception.Message}");
        }
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
        if (eventArgs.ChangedButton != MouseButton.Left || eventArgs.ClickCount != 2)
        {
            return;
        }

        if (!FullScreenButton.IsEnabled)
        {
            return;
        }

        eventArgs.Handled = true;
        RequestFullScreenToggle();
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

    private void OnVlcPlaying(object? sender, EventArgs eventArgs)
    {
        DispatchPlayerEvent(OnPlayerPlaying);
    }

    private void OnVlcTimeChanged(object? sender, EventArgs eventArgs)
    {
        if (_isPriming)
        {
            DispatchPlayerEvent(RequestPrimingPause);
        }
    }

    private void OnVlcVideoOutputChanged(object? sender, EventArgs eventArgs)
    {
        if (_isPriming)
        {
            DispatchPlayerEvent(RequestPrimingPause);
        }
    }

    private void OnVlcPaused(object? sender, EventArgs eventArgs)
    {
        DispatchPlayerEvent(BeginPrimingSeek);
    }

    private void OnVlcLengthChanged(object? sender, EventArgs eventArgs)
    {
        DispatchPlayerEvent(UpdateDurationFromPlayer);
    }

    private void OnVlcEndReached(object? sender, EventArgs eventArgs)
    {
        DispatchPlayerEvent(OnPlayerMediaEnded);
    }

    private void OnVlcEncounteredError(object? sender, EventArgs eventArgs)
    {
        DispatchPlayerEvent(() => ShowPlaybackError("This recording could not be played by VLC."));
    }

    private void OnPlayerPlaying()
    {
        if (!_hasAssignedMedia)
        {
            return;
        }

        if (_isPriming)
        {
            return;
        }

        _hasMedia = true;
        UpdateDurationFromPlayer();

        if (_isPrimedPaused || !_isPlaybackRequested)
        {
            return;
        }

        var appliedPendingPosition = ApplyPendingPlaybackStartPosition();

        PlayPauseButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        _isPlaying = true;
        UpdatePlayPauseButton();
        PlayerPreviewCover.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        _positionTimer.Start();
        if (!appliedPendingPosition)
        {
            UpdatePositionFromPlayer();
        }
    }

    private void RequestPrimingPause()
    {
        if (!_isPriming || _isPrimingPauseRequested || _mediaPlayer is null || !_hasAssignedMedia)
        {
            return;
        }

        _isPrimingPauseRequested = true;
        _mediaPlayer.SetPause(true);
    }

    private void BeginPrimingSeek()
    {
        if (
            !_isPriming
            || !_isPrimingPauseRequested
            || _isPrimingSeekPending
            || _mediaPlayer is null
            || !_hasAssignedMedia
        )
        {
            return;
        }

        _isPrimingSeekPending = true;
        _previewSeekPollCount = 0;
        _previewSeekStablePollCount = 0;
        _mediaPlayer.Time = 0;
        _previewSeekTimer.Start();
    }

    private void OnPreviewSeekTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!_isPriming || !_isPrimingSeekPending || _mediaPlayer is null || !_hasAssignedMedia)
        {
            _previewSeekTimer.Stop();
            return;
        }

        _previewSeekPollCount++;
        if (
            _mediaPlayer.State == VlcState.Paused
            && _mediaPlayer.Time is >= 0 and <= PreviewStartToleranceMilliseconds
        )
        {
            _previewSeekStablePollCount++;
        }
        else
        {
            _previewSeekStablePollCount = 0;
        }

        if (
            _previewSeekStablePollCount < PreviewSeekStablePollCount
            && _previewSeekPollCount < PreviewSeekMaxPollCount
        )
        {
            return;
        }

        CompletePriming();
    }

    private void CompletePriming()
    {
        if (!_isPriming || !_isPrimingSeekPending || _mediaPlayer is null || !_hasAssignedMedia)
        {
            return;
        }

        ResetPrimingState();
        _hasMedia = true;
        _hasPlaybackEnded = false;
        _isPlaying = false;
        _isPlaybackRequested = false;
        _isPrimedPaused = true;
        PlayPauseButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        UpdatePlayPauseButton();
        UpdateDurationFromPlayer();
        UpdatePositionFromPlayer();
        PlayerPreviewCover.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
    }

    private void OnPlayerMediaEnded()
    {
        ResetPrimingState();
        _isPrimedPaused = false;
        _hasPlaybackEnded = true;
        _pendingPlaybackStartPosition = null;
        _isPlaybackRequested = false;
        ApplyPlayerAudioState();
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayPauseButton();
        PlaybackSlider.Value = PlaybackSlider.Maximum;
        UpdatePlaybackTimeText(GetDuration(), GetDuration());
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
        if (!_isSeeking)
        {
            UpdatePositionFromPlayer();
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

    private void OnPlaybackSliderPreviewMouseUp(object sender, MouseButtonEventArgs eventArgs)
    {
        SeekToSliderValue();
        _isSeeking = false;
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
    }

    private void SeekToSliderValue()
    {
        if (!_hasMedia)
        {
            return;
        }

        var position = TimeSpan.FromSeconds(PlaybackSlider.Value);
        SetPosition(position);
        UpdatePlaybackTimeText(position, GetDuration());
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
        var position = GetPosition();
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, GetDuration());
    }

    private void StopPlaybackCore()
    {
        ResetPrimingState();
        _isPrimedPaused = false;
        _hasPlaybackEnded = false;
        _pendingPlaybackStartPosition = null;
        _isPlaybackRequested = false;
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayPauseButton();
        if (_mediaPlayer is not null && _hasAssignedMedia)
        {
            _mediaPlayer.Stop();
        }

        ApplyPlayerAudioState();
    }

    private void StartPlayback()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        var duration = GetDuration();
        if (_hasPlaybackEnded || (duration > TimeSpan.Zero && GetPosition() >= duration))
        {
            var playbackStartPosition = Clamp(
                _pendingPlaybackStartPosition ?? TimeSpan.Zero,
                TimeSpan.Zero,
                duration
            );
            _mediaPlayer.Stop();
            _pendingPlaybackStartPosition =
                playbackStartPosition > TimeSpan.Zero ? playbackStartPosition : null;
            PlaybackSlider.Value = Math.Min(
                PlaybackSlider.Maximum,
                playbackStartPosition.TotalSeconds
            );
            UpdatePlaybackTimeText(playbackStartPosition, duration);
        }

        _hasPlaybackEnded = false;
        ApplyPlayerAudioState();
        _isPrimedPaused = false;
        _isPlaybackRequested = true;
        if (!_mediaPlayer.Play())
        {
            ShowPlaybackError("This recording could not be played by VLC.");
            return;
        }

        _isPlaying = true;
        UpdatePlayPauseButton();
        _positionTimer.Start();
    }

    private void PausePlayback()
    {
        _isPlaybackRequested = false;
        _mediaPlayer?.SetPause(true);
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

    private bool EnsurePlayer()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (_mediaPlayer is not null)
        {
            return true;
        }

        try
        {
            _libVlc = new VlcInstance();
            _mediaPlayer = new VlcMediaPlayer(_libVlc);
            _playerLifetimeVersion++;
            _mediaPlayer.Playing += OnVlcPlaying;
            _mediaPlayer.Paused += OnVlcPaused;
            _mediaPlayer.TimeChanged += OnVlcTimeChanged;
            _mediaPlayer.Vout += OnVlcVideoOutputChanged;
            _mediaPlayer.LengthChanged += OnVlcLengthChanged;
            _mediaPlayer.EndReached += OnVlcEndReached;
            _mediaPlayer.EncounteredError += OnVlcEncounteredError;
            VideoView.MediaPlayer = _mediaPlayer;
            ApplyPlayerAudioState();
            return true;
        }
        catch (Exception exception)
        {
            DisposePlayer();
            ShowPlaybackError($"VLC could not be initialized: {exception.Message}");
            return false;
        }
    }

    private void DisposePlayer()
    {
        _playerLifetimeVersion++;
        var mediaPlayer = _mediaPlayer;
        _mediaPlayer = null;

        if (mediaPlayer is not null)
        {
            mediaPlayer.Playing -= OnVlcPlaying;
            mediaPlayer.Paused -= OnVlcPaused;
            mediaPlayer.TimeChanged -= OnVlcTimeChanged;
            mediaPlayer.Vout -= OnVlcVideoOutputChanged;
            mediaPlayer.LengthChanged -= OnVlcLengthChanged;
            mediaPlayer.EndReached -= OnVlcEndReached;
            mediaPlayer.EncounteredError -= OnVlcEncounteredError;
        }

        VideoView.MediaPlayer = null;
        mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _libVlc = null;
        _hasAssignedMedia = false;
        _assignedSource = null;
    }

    private void ReleaseMedia()
    {
        _hasAssignedMedia = false;
        _assignedSource = null;

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Media = null;
        }
    }

    private void DispatchPlayerEvent(Action action)
    {
        var playerLifetimeVersion = _playerLifetimeVersion;
        var sourceLoadVersion = _sourceLoadVersion;

        Dispatcher.InvokeAsync(
            () =>
            {
                if (
                    _mediaPlayer is null
                    || playerLifetimeVersion != _playerLifetimeVersion
                    || sourceLoadVersion != _sourceLoadVersion
                )
                {
                    return;
                }

                action();
            },
            DispatcherPriority.Background
        );
    }

    private void ResetPlayerState(bool sourceAvailable)
    {
        _hasMedia = false;
        _hasPlaybackEnded = false;
        _pendingPlaybackStartPosition = null;
        ResetPrimingState();
        _isPlaybackRequested = false;
        _isPrimedPaused = false;
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

    private void ResetPrimingState()
    {
        _isPriming = false;
        _isPrimingPauseRequested = false;
        _isPrimingSeekPending = false;
        _previewSeekTimer.Stop();
        _previewSeekPollCount = 0;
        _previewSeekStablePollCount = 0;
    }

    private void UpdateDurationFromPlayer()
    {
        var duration = GetDuration();
        PlaybackSlider.Maximum = duration.TotalSeconds;
        PlaybackSlider.IsEnabled = _hasMedia && duration > TimeSpan.Zero;
    }

    private void ApplyPlayerAudioState()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        if (_isPriming)
        {
            _mediaPlayer.Volume = 0;
            _mediaPlayer.Mute = true;
            return;
        }

        _mediaPlayer.Volume = (int)Math.Round(_volume * VolumeSliderScale);
        _mediaPlayer.Mute = IsEffectivelyMuted();
    }

    private TimeSpan GetPosition()
    {
        var time = _mediaPlayer?.Time ?? -1;
        return time > 0 ? TimeSpan.FromMilliseconds(time) : TimeSpan.Zero;
    }

    private void SetPosition(TimeSpan position)
    {
        if (_mediaPlayer is not null)
        {
            var positionMilliseconds = (long)Math.Round(Math.Max(0, position.TotalMilliseconds));
            var duration = GetDuration();

            if (_hasPlaybackEnded || _pendingPlaybackStartPosition is not null)
            {
                _pendingPlaybackStartPosition =
                    duration > TimeSpan.Zero && positionMilliseconds >= duration.TotalMilliseconds
                        ? TimeSpan.Zero
                        : TimeSpan.FromMilliseconds(positionMilliseconds);
                return;
            }

            _mediaPlayer.Time = positionMilliseconds;
        }
    }

    private bool ApplyPendingPlaybackStartPosition()
    {
        if (_mediaPlayer is null || _pendingPlaybackStartPosition is not { } position)
        {
            return false;
        }

        _pendingPlaybackStartPosition = null;
        _mediaPlayer.Time = (long)Math.Round(position.TotalMilliseconds);
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, GetDuration());
        return true;
    }

    private TimeSpan GetDuration()
    {
        var length = _mediaPlayer?.Length ?? -1;
        return length > 0 ? TimeSpan.FromMilliseconds(length) : TimeSpan.Zero;
    }

    private bool IsEffectivelyMuted()
    {
        return _isMuted || _volume <= 0;
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
