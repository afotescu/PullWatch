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

            var settings = settingsProvider.Current;
            LogRecorderPreflight(context);

            var sourceLookupTimestamp = Stopwatch.GetTimestamp();
            var (wowProcess, recordingSource, sourceDescription) = CreateRecordingSource(settings);
            _wowProcess = wowProcess;
            logger.LogInformation(
                "Located recording source {RecordingSource} in {ElapsedMilliseconds:F1} ms",
                sourceDescription,
                Stopwatch.GetElapsedTime(sourceLookupTimestamp).TotalMilliseconds
            );

            var outputPath = CreateOutputPath(context, settings);

            var recorderCreationTimestamp = Stopwatch.GetTimestamp();
            var recorder = Recorder.CreateRecorder(CreateOptions(recordingSource, settings));
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

    private static RecorderOptions CreateOptions(
        RecordingSourceBase recordingSource,
        PullWatchSettings settings
    )
    {
        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [recordingSource] },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled =
                    settings.Audio.CaptureSystemAudio || settings.Audio.CaptureMicrophone,
                IsOutputDeviceEnabled = settings.Audio.CaptureSystemAudio,
                IsInputDeviceEnabled = settings.Audio.CaptureMicrophone,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder(),
                Bitrate = settings.Video.Bitrate,
                Framerate = settings.Video.FrameRate,
                IsHardwareEncodingEnabled = true,
                IsThrottlingDisabled = false,
            },
        };
    }

    private static (
        Process? WowProcess,
        RecordingSourceBase RecordingSource,
        string Description
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
            "World of Warcraft window"
        );
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
        var recordingsDirectory =
            settings.RecordingsDirectory
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
