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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            if (_recorder is not null)
            {
                logger.LogWarning("Ignoring recording start because a recording is already active");
                return;
            }

            var windowHandle = FindWowWindowHandle();
            var outputPath = CreateOutputPath();
            var recorder = Recorder.CreateRecorder(CreateOptions(windowHandle));

            _recordingFinished = CreateCompletionSource();
            _isStopping = false;
            _outputPath = outputPath;
            _recorder = recorder;

            recorder.OnRecordingComplete += OnRecordingComplete;
            recorder.OnRecordingFailed += OnRecordingFailed;

            logger.LogInformation("Starting recording: {OutputPath}", outputPath);
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
                logger.LogInformation("Stopping recording: {OutputPath}", _outputPath);
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
        logger.LogInformation("Recording completed: {OutputPath}", eventArgs.FilePath);
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
        recorder.Dispose();
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
