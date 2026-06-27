using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ScreenRecorderLib;

namespace PullWatch;

public sealed class ScreenRecordingService(
    SettingsProvider settingsProvider,
    ILogger<ScreenRecordingService> logger
) : IRecordingService
{
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private Recorder? _recorder;
    private Process? _wowProcess;
    private TaskCompletionSource _recordingStarted = CreateCompletionSource();
    private TaskCompletionSource _recordingFinished = CreateCompletionSource();
    private bool _isStopping;
    private string? _outputPath;
    private long _recordingRequestedTimestamp;
    private long _recordingStartedTimestamp;
    private long _stopRequestedTimestamp;

    public event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    public event EventHandler? CaptureTargetExited;

    public string? ActiveOutputPath => Volatile.Read(ref _outputPath);

    public async Task StartAsync(RecordingContext context, CancellationToken cancellationToken)
    {
        var startRequestTimestamp = Stopwatch.GetTimestamp();
        Task recordingStarted;

        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            if (_recorder is not null)
            {
                logger.LogWarning("Ignoring recording start because a recording is already active");
                return;
            }

            var settings = settingsProvider.Current;
            LogRecorderPreflight(context);

            var sourceLookupTimestamp = Stopwatch.GetTimestamp();
            var (wowProcess, recordingSource, sourceDescription, captureSize) =
                CreateRecordingSource(settings);
            _wowProcess = wowProcess;
            logger.LogInformation(
                "Located recording source {RecordingSource} at {CaptureWidth}x{CaptureHeight} in {ElapsedMilliseconds:F1} ms",
                sourceDescription,
                captureSize.Width,
                captureSize.Height,
                Stopwatch.GetElapsedTime(sourceLookupTimestamp).TotalMilliseconds
            );

            var outputPath = CreateOutputPath(context, settings);

            var recorderCreationTimestamp = Stopwatch.GetTimestamp();
            var recorderOptions = CreateOptions(recordingSource, settings, captureSize);
            var outputSize = CalculateOutputSize(settings, captureSize);
            LogVideoEncoderOptions(
                settings,
                captureSize,
                outputSize,
                recorderOptions.VideoEncoderOptions
            );
            var recorder = Recorder.CreateRecorder(recorderOptions);
            logger.LogInformation(
                "Created screen recorder in {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(recorderCreationTimestamp).TotalMilliseconds
            );

            _recordingStarted = CreateCompletionSource();
            _recordingFinished = CreateCompletionSource();
            _isStopping = false;
            _outputPath = outputPath;
            _recorder = recorder;
            _recordingRequestedTimestamp = startRequestTimestamp;
            _recordingStartedTimestamp = 0;
            _stopRequestedTimestamp = 0;

            recorder.OnRecordingComplete += OnRecordingComplete;
            recorder.OnRecordingFailed += OnRecordingFailed;
            recorder.OnStatusChanged += OnStatusChanged;
            if (wowProcess is not null)
            {
                wowProcess.Exited += OnWowProcessExited;
                wowProcess.EnableRaisingEvents = true;
            }

            logger.LogInformation(
                "Requesting recording start after {ElapsedMilliseconds:F1} ms: {OutputPath}",
                Stopwatch.GetElapsedTime(startRequestTimestamp).TotalMilliseconds,
                outputPath
            );
            recorder.Record(outputPath);
            recordingStarted = _recordingStarted.Task;
        }
        catch (Exception exception)
        {
            DisposeRecorder();
            throw RecordingFailureClassifier.Classify(exception);
        }
        finally
        {
            _stateLock.Release();
        }

        await recordingStarted.WaitAsync(cancellationToken);
    }

    private void LogRecorderPreflight(RecordingContext context)
    {
        var appBaseDirectory = AppContext.BaseDirectory;
        var recorderAssemblyPath = Path.Combine(appBaseDirectory, "ScreenRecorderLib.dll");
        var recorderAssemblyStatus = GetFileStatus(recorderAssemblyPath);
        var processPath = Environment.ProcessPath ?? "unknown";
        var isElevated = IsCurrentProcessElevated();

        logger.LogInformation(
            "Recorder startup preflight for {RecordingContext}: process {ProcessPath}, base directory {BaseDirectory}, elevated {IsElevated}, ScreenRecorderLib.dll {RecorderAssemblyStatus} at {RecorderAssemblyPath}",
            context.GetType().Name,
            processPath,
            appBaseDirectory,
            isElevated,
            recorderAssemblyStatus,
            recorderAssemblyPath
        );
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task recordingFinished;

        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            if (_recorder is null)
            {
                logger.LogDebug("Ignoring recording stop because no recording is active");
                return;
            }

            recordingFinished = _recordingFinished.Task;

            if (!_isStopping)
            {
                _isStopping = true;
                _stopRequestedTimestamp = Stopwatch.GetTimestamp();
                logger.LogInformation(
                    "Requesting recording stop after {RecordingDuration}: {OutputPath}",
                    GetRecordingDuration(),
                    _outputPath
                );
                _recorder.Stop();
            }
        }
        catch (Exception exception)
        {
            _recordingStarted.TrySetException(exception);
            _recordingFinished.TrySetException(exception);
            DisposeRecorder();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }

        await recordingFinished.WaitAsync(cancellationToken);
        DisposeRecorder();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            _stateLock.Dispose();
        }
    }

    internal static RecorderOptions CreateOptions(
        RecordingSourceBase recordingSource,
        PullWatchSettings settings,
        VideoCaptureSize captureSize
    )
    {
        var outputSize = CalculateOutputSize(settings, captureSize);

        return new RecorderOptions
        {
            SourceOptions = CreateSourceOptions(recordingSource, outputSize, captureSize),
            AudioOptions = CreateAudioOptions(settings),
            OutputOptions = CreateOutputOptions(outputSize, captureSize),
            VideoEncoderOptions = CreateVideoEncoderOptions(settings, captureSize),
        };
    }

    private static OutputOptions CreateOutputOptions(
        VideoCaptureSize outputSize,
        VideoCaptureSize captureSize
    )
    {
        var options = new OutputOptions();

        if (outputSize == captureSize)
        {
            return options;
        }

        options.OutputFrameSize = new ScreenSize(outputSize.Width, outputSize.Height);
        options.Stretch = StretchMode.Fill;
        return options;
    }

    private static SourceOptions CreateSourceOptions(
        RecordingSourceBase recordingSource,
        VideoCaptureSize outputSize,
        VideoCaptureSize captureSize
    )
    {
        ConfigureOutputScaling(recordingSource, outputSize, captureSize);
        return new SourceOptions { RecordingSources = [recordingSource] };
    }

    private static void ConfigureOutputScaling(
        RecordingSourceBase recordingSource,
        VideoCaptureSize outputSize,
        VideoCaptureSize captureSize
    )
    {
        if (outputSize == captureSize)
        {
            return;
        }

        recordingSource.OutputSize = new ScreenSize(outputSize.Width, outputSize.Height);
        recordingSource.Stretch = StretchMode.Fill;
    }

    internal static AudioOptions CreateAudioOptions(PullWatchSettings settings)
    {
        return new AudioOptions
        {
            IsAudioEnabled = settings.Audio.CaptureSystemAudio || settings.Audio.CaptureMicrophone,
            IsOutputDeviceEnabled = settings.Audio.CaptureSystemAudio,
            IsInputDeviceEnabled = settings.Audio.CaptureMicrophone,
            Bitrate = AudioBitrate.bitrate_96kbps,
            Channels = AudioChannels.Stereo,
        };
    }

    internal static VideoEncoderOptions CreateVideoEncoderOptions(
        PullWatchSettings settings,
        VideoCaptureSize captureSize
    )
    {
        var outputSize = CalculateOutputSize(settings, captureSize);

        return new VideoEncoderOptions
        {
            Encoder = new H264VideoEncoder
            {
                BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
            },
            Bitrate = VideoBitrateCalculator.CalculateBitrate(
                outputSize,
                settings.Video.FrameRate,
                settings.Video.Quality
            ),
            Framerate = settings.Video.FrameRate,
            IsHardwareEncodingEnabled = true,
            IsLowLatencyEnabled = false,
            IsFixedFramerate = false,
            IsThrottlingDisabled = false,
            IsFragmentedMp4Enabled = true,
            IsMp4FastStartEnabled = false,
        };
    }

    internal static VideoCaptureSize CalculateOutputSize(
        PullWatchSettings settings,
        VideoCaptureSize captureSize
    )
    {
        ArgumentNullException.ThrowIfNull(settings);

        return VideoOutputSizeCalculator.CalculateOutputSize(captureSize, settings.Video.Scaling);
    }

    private static (
        Process? WowProcess,
        RecordingSourceBase RecordingSource,
        string Description,
        VideoCaptureSize CaptureSize
    ) CreateRecordingSource(PullWatchSettings settings)
    {
        var (wowProcess, windowHandle) = FindWowProcess();
        return (
            wowProcess,
            new WindowRecordingSource(windowHandle)
            {
                IsBorderRequired = settings.Video.ShowCaptureBorder,
                IsCursorCaptureEnabled = settings.Video.CaptureCursor,
            },
            "World of Warcraft window",
            WowWindowCaptureSizeDetector.GetCaptureSize(windowHandle)
        );
    }

    private void LogVideoEncoderOptions(
        PullWatchSettings settings,
        VideoCaptureSize captureSize,
        VideoCaptureSize outputSize,
        VideoEncoderOptions options
    )
    {
        var bitrateMode = options.Encoder is H264VideoEncoder encoder
            ? encoder.BitrateMode.ToString()
            : "unknown";

        logger.LogInformation(
            "Video encoder settings: H.264 {BitrateMode}, {VideoQuality}, {VideoScaling}, capture {CaptureWidth}x{CaptureHeight}, output {OutputWidth}x{OutputHeight}, {FrameRate} FPS, {BitrateMegabits} Mbps target, fragmented MP4 {FragmentedMp4Enabled}",
            bitrateMode,
            settings.Video.Quality,
            settings.Video.Scaling,
            captureSize.Width,
            captureSize.Height,
            outputSize.Width,
            outputSize.Height,
            options.Framerate,
            VideoBitrateCalculator.ToMegabitsPerSecond(options.Bitrate),
            options.IsFragmentedMp4Enabled
        );
    }

    private static (Process Process, nint WindowHandle) FindWowProcess()
    {
        if (WowWindowCaptureSizeDetector.TryFindWowWindow(out var process, out var windowHandle))
        {
            return (process, windowHandle);
        }

        throw new CaptureTargetUnavailableException(
            "Could not find a running World of Warcraft window."
        );
    }

    internal static string CreateOutputPath(RecordingContext context, PullWatchSettings settings)
    {
        var recordingsDirectory =
            settings.RecordingsDirectory
            ?? throw new InvalidOperationException("Recordings directory was not configured.");

        try
        {
            Directory.CreateDirectory(recordingsDirectory);
        }
        catch (Exception exception) when (IsDirectoryUnavailableException(exception))
        {
            throw new RecordingOutputUnavailableException(recordingsDirectory, exception);
        }

        return RecordingFilenameBuilder.CreateAvailablePath(recordingsDirectory, context);
    }

    private static bool IsDirectoryUnavailableException(Exception exception)
    {
        return exception
            is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or DirectoryNotFoundException;
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs eventArgs)
    {
        logger.LogInformation(
            "Recording completed; duration {RecordingDuration}, finalization {FinalizationDuration}, size {FileSizeMegabytes:F1} MB: {OutputPath}",
            GetRecordingDuration(),
            GetElapsedTime(_stopRequestedTimestamp),
            GetFileSizeMegabytes(eventArgs.FilePath),
            eventArgs.FilePath
        );

        _recordingStarted.TrySetException(
            new InvalidOperationException(
                "Recording completed before the recorder confirmed startup."
            )
        );
        CompleteRecording();

        if (!Volatile.Read(ref _isStopping))
        {
            QueueRecorderCleanup("Recorder completed without an active stop request");
        }
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        var exception = RecordingFailureClassifier.Classify(
            new InvalidOperationException(eventArgs.Error)
        );

        logger.LogError(
            "Recording failed for {OutputPath}: {RecordingError}",
            eventArgs.FilePath,
            eventArgs.Error
        );

        _recordingStarted.TrySetException(exception);
        _recordingFinished.TrySetException(exception);
        QueueRecorderCleanup("Recorder failed");
        Failed?.Invoke(this, new RecordingServiceFailedEventArgs(exception));
    }

    private void OnWowProcessExited(object? sender, EventArgs eventArgs)
    {
        logger.LogInformation("World of Warcraft exited while a recording was active");
        CaptureTargetExited?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        if (eventArgs.Status == RecorderStatus.Recording && _recordingStartedTimestamp == 0)
        {
            _recordingStartedTimestamp = Stopwatch.GetTimestamp();
            _recordingStarted.TrySetResult();
            logger.LogInformation(
                "Recorder status changed to {RecorderStatus} after {StartupDuration}",
                eventArgs.Status,
                GetElapsedTime(_recordingRequestedTimestamp)
            );
            return;
        }

        logger.LogInformation(
            "Recorder status changed to {RecorderStatus}; recording duration {RecordingDuration}",
            eventArgs.Status,
            GetRecordingDuration()
        );
    }

    private void CompleteRecording()
    {
        _recordingFinished.TrySetResult();
    }

    private void QueueRecorderCleanup(string reason)
    {
        _ = Task.Run(() =>
        {
            try
            {
                DisposeRecorder();
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "{RecorderCleanupReason}; recorder cleanup failed",
                    reason
                );
            }
        });
    }

    private void DisposeRecorder()
    {
        var recorder = Interlocked.Exchange(ref _recorder, null);
        var wowProcess = Interlocked.Exchange(ref _wowProcess, null);
        _isStopping = false;
        _outputPath = null;

        if (wowProcess is not null)
        {
            wowProcess.Exited -= OnWowProcessExited;
            wowProcess.Dispose();
        }

        if (recorder is not null)
        {
            recorder.OnRecordingComplete -= OnRecordingComplete;
            recorder.OnRecordingFailed -= OnRecordingFailed;
            recorder.OnStatusChanged -= OnStatusChanged;
            recorder.Dispose();
        }
    }

    private TimeSpan GetRecordingDuration()
    {
        return GetElapsedTime(
            _recordingStartedTimestamp != 0
                ? _recordingStartedTimestamp
                : _recordingRequestedTimestamp
        );
    }

    private static TimeSpan GetElapsedTime(long timestamp)
    {
        return timestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(timestamp);
    }

    private static string GetFileStatus(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? $"exists, {fileInfo.Length} bytes" : "missing";
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"unreadable: {exception.Message}";
        }
        catch (Exception exception)
        {
            return $"unknown: {exception.GetType().Name}: {exception.Message}";
        }
    }

    private static bool? IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double GetFileSizeMegabytes(string path)
    {
        try
        {
            return new FileInfo(path).Length / 1024d / 1024d;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
