using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ScreenRecorderLib;

namespace PullWatch;

public sealed class ScreenRecordingService(
    SettingsProvider settingsProvider,
    ILogger<ScreenRecordingService> logger) : IRecordingService
{
    private const string WowProcessName = "Wow";

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

            var wowLookupTimestamp = Stopwatch.GetTimestamp();
            var (wowProcess, windowHandle) = FindWowProcess();
            _wowProcess = wowProcess;
            logger.LogInformation(
                "Located World of Warcraft window in {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(wowLookupTimestamp).TotalMilliseconds);

            var settings = settingsProvider.Current;
            var outputPath = CreateOutputPath(context, settings);

            var recorderCreationTimestamp = Stopwatch.GetTimestamp();
            var recorder = Recorder.CreateRecorder(CreateOptions(windowHandle, settings));
            logger.LogInformation(
                "Created screen recorder in {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(recorderCreationTimestamp).TotalMilliseconds);

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
            wowProcess.Exited += OnWowProcessExited;
            wowProcess.EnableRaisingEvents = true;

            logger.LogInformation(
                "Requesting recording start after {ElapsedMilliseconds:F1} ms: {OutputPath}",
                Stopwatch.GetElapsedTime(startRequestTimestamp).TotalMilliseconds,
                outputPath);
            recorder.Record(outputPath);
            recordingStarted = _recordingStarted.Task;
        }
        catch
        {
            DisposeRecorder();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }

        await recordingStarted.WaitAsync(cancellationToken);
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
                    _outputPath);
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

    private static RecorderOptions CreateOptions(nint windowHandle, PullWatchSettings settings)
    {
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources =
                [
                    new WindowRecordingSource(windowHandle)
                    {
                        IsBorderRequired = settings.Video.ShowCaptureBorder,
                        IsCursorCaptureEnabled = settings.Video.CaptureCursor
                    }
                ]
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled =
                    settings.Audio.CaptureSystemAudio ||
                    settings.Audio.CaptureMicrophone,
                IsOutputDeviceEnabled = settings.Audio.CaptureSystemAudio,
                IsInputDeviceEnabled = settings.Audio.CaptureMicrophone
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder(),
                Bitrate = settings.Video.Bitrate,
                Framerate = settings.Video.FrameRate,
                IsHardwareEncodingEnabled = true,
                IsThrottlingDisabled = false
            }
        };
    }

    private static (Process Process, nint WindowHandle) FindWowProcess()
    {
        foreach (var process in Process.GetProcessesByName(WowProcessName))
        {
            try
            {
                var windowHandle = process.MainWindowHandle;

                if (windowHandle != nint.Zero)
                {
                    return (process, windowHandle);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being inspected.
            }

            process.Dispose();
        }

        throw new InvalidOperationException("Could not find a running World of Warcraft window.");
    }

    private static string CreateOutputPath(RecordingContext context, PullWatchSettings settings)
    {
        var recordingsDirectory = settings.RecordingsDirectory
            ?? throw new InvalidOperationException("Recordings directory was not configured.");

        Directory.CreateDirectory(recordingsDirectory);

        return RecordingFilenameBuilder.CreateAvailablePath(recordingsDirectory, context);
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs eventArgs)
    {
        logger.LogInformation(
            "Recording completed; duration {RecordingDuration}, finalization {FinalizationDuration}, size {FileSizeMegabytes:F1} MB: {OutputPath}",
            GetRecordingDuration(),
            GetElapsedTime(_stopRequestedTimestamp),
            GetFileSizeMegabytes(eventArgs.FilePath),
            eventArgs.FilePath);

        _recordingStarted.TrySetException(
            new InvalidOperationException("Recording completed before the recorder confirmed startup."));
        CompleteRecording();
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        var exception = new InvalidOperationException(eventArgs.Error);

        logger.LogError(
            "Recording failed for {OutputPath}: {RecordingError}",
            eventArgs.FilePath,
            eventArgs.Error);

        _recordingStarted.TrySetException(exception);
        _recordingFinished.TrySetException(exception);
        DisposeRecorder();
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
                GetElapsedTime(_recordingRequestedTimestamp));
            return;
        }

        logger.LogInformation(
            "Recorder status changed to {RecorderStatus}; recording duration {RecordingDuration}",
            eventArgs.Status,
            GetRecordingDuration());
    }

    private void CompleteRecording()
    {
        _recordingFinished.TrySetResult();
        DisposeRecorder();
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
                : _recordingRequestedTimestamp);
    }

    private static TimeSpan GetElapsedTime(long timestamp)
    {
        return timestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(timestamp);
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
