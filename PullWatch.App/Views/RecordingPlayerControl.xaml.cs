using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PullWatch;

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
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _isSeeking;
    private bool _isUpdatingVolumeControls;
    private double _lastAudibleVolume;
    private int _sourceLoadVersion;
    private string? _playbackErrorText;

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
        PlaybackSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(OnPlaybackThumbDragStarted)
        );
        PlaybackSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnPlaybackThumbDragCompleted)
        );
        _lastAudibleVolume = GetInitialAudibleVolume();
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
        MediaPlayer.Source = null;
        FullScreenButton.IsEnabled = false;
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

        var position = Clamp(MediaPlayer.Position + offset, TimeSpan.Zero, duration);
        MediaPlayer.Position = position;
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, duration);

        return true;
    }

    public bool AdjustVolume(double delta)
    {
        SetVolume(MediaPlayer.Volume + delta, unmute: true);

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
        if (Source is not null && MediaPlayer.Source is null)
        {
            ScheduleLoadSource(Source);
        }
    }

    private void ScheduleLoadSource(Uri? source)
    {
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
        return IsLoaded && PresentationSource.FromVisual(MediaPlayer) is not null;
    }

    private void LoadSource(Uri? source)
    {
        StopPlaybackCore();
        _hasMedia = false;
        _isSeeking = false;
        _playbackErrorText = null;
        PlayPauseButton.IsEnabled = false;
        FullScreenButton.IsEnabled = source is not null;
        PlaybackSlider.IsEnabled = false;
        PlaybackSlider.Maximum = 0;
        PlaybackSlider.Value = 0;
        UpdatePlaybackTimeText(TimeSpan.Zero, TimeSpan.Zero);
        UpdatePlaceholderText();
        PlayerPlaceholder.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        MediaPlayer.Source = source;

        if (source is not null)
        {
            MediaPlayer.Play();
        }
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs eventArgs)
    {
        TogglePlayback();
    }

    private void OnFullScreenClicked(object sender, RoutedEventArgs eventArgs)
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
        _hasMedia = true;
        PlayPauseButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        PlayerPlaceholder.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);

        if (MediaPlayer.NaturalDuration.HasTimeSpan)
        {
            var duration = MediaPlayer.NaturalDuration.TimeSpan;
            PlaybackSlider.Maximum = duration.TotalSeconds;
            PlaybackSlider.IsEnabled = duration > TimeSpan.Zero;
        }

        if (_isPlaying)
        {
            MediaPlayer.Play();
            _positionTimer.Start();
        }
        else
        {
            MediaPlayer.Pause();
            MediaPlayer.Position = TimeSpan.Zero;
        }

        UpdatePositionFromPlayer();
    }

    private void OnPlayerMediaEnded(object sender, RoutedEventArgs eventArgs)
    {
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayPauseButton();
        PlaybackSlider.Value = PlaybackSlider.Maximum;
        UpdatePlaybackTimeText(GetDuration(), GetDuration());
    }

    private void OnPlayerMediaFailed(object sender, ExceptionRoutedEventArgs eventArgs)
    {
        StopPlaybackCore();
        _hasMedia = false;
        _playbackErrorText =
            $"This recording could not be played: {eventArgs.ErrorException.Message}";
        PlayPauseButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        PlaybackSlider.IsEnabled = false;
        UpdatePlaceholderText();
        PlayerPlaceholder.SetCurrentValue(VisibilityProperty, Visibility.Visible);
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
        MediaPlayer.Position = position;
        UpdatePlaybackTimeText(position, GetDuration());
    }

    private void ToggleMute()
    {
        if (IsEffectivelyMuted())
        {
            MediaPlayer.Volume = _lastAudibleVolume;
            MediaPlayer.IsMuted = false;
        }
        else
        {
            _lastAudibleVolume = MediaPlayer.Volume;
            MediaPlayer.IsMuted = true;
        }

        UpdateVolumeControls();
    }

    private void SetVolume(double volume, bool unmute)
    {
        var clampedVolume = Math.Clamp(volume, 0, 1);
        MediaPlayer.Volume = clampedVolume;

        if (clampedVolume > 0)
        {
            _lastAudibleVolume = clampedVolume;
        }

        if (unmute && clampedVolume > 0)
        {
            MediaPlayer.IsMuted = false;
        }
        else if (clampedVolume <= 0)
        {
            MediaPlayer.IsMuted = true;
        }

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
        var position = MediaPlayer.Position;
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, GetDuration());
    }

    private void StopPlaybackCore()
    {
        _positionTimer.Stop();
        _isPlaying = false;
        UpdatePlayPauseButton();
        if (MediaPlayer.Source is not null)
        {
            MediaPlayer.Stop();
        }
    }

    private void StartPlayback()
    {
        MediaPlayer.Play();
        _isPlaying = true;
        UpdatePlayPauseButton();
        _positionTimer.Start();
    }

    private void PausePlayback()
    {
        MediaPlayer.Pause();
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
        VolumeSlider.Value = Math.Round(MediaPlayer.Volume * VolumeSliderScale);
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

    private TimeSpan GetDuration()
    {
        return MediaPlayer.NaturalDuration.HasTimeSpan
            ? MediaPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
    }

    private bool IsEffectivelyMuted()
    {
        return MediaPlayer.IsMuted || MediaPlayer.Volume <= 0;
    }

    private double GetInitialAudibleVolume()
    {
        return MediaPlayer.Volume > 0 ? MediaPlayer.Volume : FallbackUnmuteVolume;
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
