using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ScreenRecorderLib;

namespace PullWatch;

public sealed class ScreenRecordingService(ILogger<ScreenRecordingService> logger) : IRecordingService
{
    private const string WowProcessName = "Wow";
    private const int VideoBitrate = 12_000_000;
    private const int VideoFramerate = 60;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private Recorder? _recorder;
    private TaskCompletionSource _recordingFinished = CreateCompletionSource();
    private bool _isStopping;
    private string? _outputPath;
    private long _recordingRequestedTimestamp;
    private long _recordingStartedTimestamp;
    private long _stopRequestedTimestamp;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var startRequestTimestamp = Stopwatch.GetTimestamp();

        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            if (_recorder is not null)
            {
                logger.LogWarning("Ignoring recording start because a recording is already active");
                return;
            }

            var wowLookupTimestamp = Stopwatch.GetTimestamp();
            var windowHandle = FindWowWindowHandle();
            logger.LogInformation(
                "Located World of Warcraft window in {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(wowLookupTimestamp).TotalMilliseconds);

            var outputPath = CreateOutputPath();

            var recorderCreationTimestamp = Stopwatch.GetTimestamp();
            var recorder = Recorder.CreateRecorder(CreateOptions(windowHandle));
            logger.LogInformation(
                "Created screen recorder in {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(recorderCreationTimestamp).TotalMilliseconds);

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

            logger.LogInformation(
                "Requesting recording start after {ElapsedMilliseconds:F1} ms: {OutputPath}",
                Stopwatch.GetElapsedTime(startRequestTimestamp).TotalMilliseconds,
                outputPath);
            recorder.Record(outputPath);
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

    private static RecorderOptions CreateOptions(nint windowHandle)
    {
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources =
                [
                    new WindowRecordingSource(windowHandle)
                    {
                        IsBorderRequired = false,
                        IsCursorCaptureEnabled = true
                    }
                ]
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = true,
                IsOutputDeviceEnabled = true,
                IsInputDeviceEnabled = false
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder(),
                Bitrate = VideoBitrate,
                Framerate = VideoFramerate,
                IsHardwareEncodingEnabled = true,
                IsThrottlingDisabled = false
            }
        };
    }

    private static nint FindWowWindowHandle()
    {
        foreach (var process in Process.GetProcessesByName(WowProcessName))
        {
            using (process)
            {
                if (process.MainWindowHandle != nint.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
        }

        throw new InvalidOperationException("Could not find a running World of Warcraft window.");
    }

    private static string CreateOutputPath()
    {
        var recordingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "PullWatch");

        Directory.CreateDirectory(recordingsDirectory);

        return Path.Combine(
            recordingsDirectory,
            $"mythic-plus_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs eventArgs)
    {
        logger.LogInformation(
            "Recording completed; duration {RecordingDuration}, finalization {FinalizationDuration}, size {FileSizeMegabytes:F1} MB: {OutputPath}",
            GetRecordingDuration(),
            GetElapsedTime(_stopRequestedTimestamp),
            GetFileSizeMegabytes(eventArgs.FilePath),
            eventArgs.FilePath);

        CompleteRecording();
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs eventArgs)
    {
        logger.LogError(
            "Recording failed for {OutputPath}: {RecordingError}",
            eventArgs.FilePath,
            eventArgs.Error);

        _recordingFinished.TrySetException(new InvalidOperationException(eventArgs.Error));
        DisposeRecorder();
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs eventArgs)
    {
        if (eventArgs.Status == RecorderStatus.Recording && _recordingStartedTimestamp == 0)
        {
            _recordingStartedTimestamp = Stopwatch.GetTimestamp();
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
        _isStopping = false;
        _outputPath = null;

        if (recorder is null)
        {
            return;
        }

        recorder.OnRecordingComplete -= OnRecordingComplete;
        recorder.OnRecordingFailed -= OnRecordingFailed;
        recorder.OnStatusChanged -= OnStatusChanged;
        recorder.Dispose();
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
