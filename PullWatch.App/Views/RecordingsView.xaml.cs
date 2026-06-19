using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PullWatch;

public partial class RecordingsView : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private RecordingsViewModel? _viewModel;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _isSeeking;

    public RecordingsView()
    {
        InitializeComponent();
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachViewModel(DataContext as RecordingsViewModel);
        LoadSelectedRecording();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        StopPlayback();
        RecordingPlayer.Source = null;
        AttachViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        AttachViewModel(eventArgs.NewValue as RecordingsViewModel);
    }

    private void AttachViewModel(RecordingsViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        LoadSelectedRecording();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(RecordingsViewModel.SelectedRecording))
        {
            LoadSelectedRecording();
        }
    }

    private void LoadSelectedRecording()
    {
        StopPlayback();
        _hasMedia = false;
        _isSeeking = false;
        PlayPauseButton.Content = "Play";
        PlaybackSlider.IsEnabled = false;
        PlaybackSlider.Maximum = 0;
        PlaybackSlider.Value = 0;
        UpdatePlaybackTimeText(TimeSpan.Zero, TimeSpan.Zero);

        var selectedRecording = _viewModel?.SelectedRecording;
        RecordingPlayer.Source = selectedRecording?.Source;
        PlayPauseButton.IsEnabled = selectedRecording is not null;
        PlayerPlaceholder.SetCurrentValue(
            TextBlock.TextProperty,
            _viewModel?.RecordingLibraryStatus ?? string.Empty
        );
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (RecordingPlayer.Source is null)
        {
            return;
        }

        if (_isPlaying)
        {
            RecordingPlayer.Pause();
            _positionTimer.Stop();
            _isPlaying = false;
            PlayPauseButton.Content = "Play";
            return;
        }

        RecordingPlayer.Play();
        _isPlaying = true;
        PlayPauseButton.Content = "Pause";
        _positionTimer.Start();
    }

    private void OnPlayerMediaOpened(object sender, RoutedEventArgs eventArgs)
    {
        _hasMedia = true;
        PlayPauseButton.IsEnabled = true;
        PlayerPlaceholder.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);

        if (RecordingPlayer.NaturalDuration.HasTimeSpan)
        {
            var duration = RecordingPlayer.NaturalDuration.TimeSpan;
            PlaybackSlider.Maximum = duration.TotalSeconds;
            PlaybackSlider.IsEnabled = duration > TimeSpan.Zero;
        }

        RecordingPlayer.Pause();
        RecordingPlayer.Position = TimeSpan.Zero;
        UpdatePositionFromPlayer();
    }

    private void OnPlayerMediaEnded(object sender, RoutedEventArgs eventArgs)
    {
        _positionTimer.Stop();
        _isPlaying = false;
        PlayPauseButton.Content = "Play";
        PlaybackSlider.Value = PlaybackSlider.Maximum;
        UpdatePlaybackTimeText(GetDuration(), GetDuration());
    }

    private void OnPlayerMediaFailed(object sender, ExceptionRoutedEventArgs eventArgs)
    {
        StopPlayback();
        _hasMedia = false;
        PlayPauseButton.IsEnabled = false;
        PlaybackSlider.IsEnabled = false;
        PlayerPlaceholder.SetCurrentValue(
            TextBlock.TextProperty,
            $"This recording could not be played: {eventArgs.ErrorException.Message}"
        );
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

    private void SeekToSliderValue()
    {
        if (!_hasMedia)
        {
            return;
        }

        var position = TimeSpan.FromSeconds(PlaybackSlider.Value);
        RecordingPlayer.Position = position;
        UpdatePlaybackTimeText(position, GetDuration());
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
        var position = RecordingPlayer.Position;
        PlaybackSlider.Value = Math.Min(PlaybackSlider.Maximum, position.TotalSeconds);
        UpdatePlaybackTimeText(position, GetDuration());
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        _isPlaying = false;
        if (RecordingPlayer.Source is not null)
        {
            RecordingPlayer.Stop();
        }
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void UpdatePlaybackTimeText(TimeSpan position, TimeSpan duration)
    {
        PlaybackTimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private TimeSpan GetDuration()
    {
        return RecordingPlayer.NaturalDuration.HasTimeSpan
            ? RecordingPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
    }
}
