using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class FfmpegRecordingService(
    SettingsProvider settingsProvider,
    ILogger<FfmpegRecordingService> logger
) : IRecordingService
{
    private const string FfmpegExecutableName = "ffmpeg";
    private const string PreferredFfmpegPath = @"C:\ffmpeg\bin\ffmpeg.exe";
    private const int StderrTailLimit = 40;
    private static readonly TimeSpan StartupConfirmationDelay = TimeSpan.FromMilliseconds(750);

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _stderrLock = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;
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
            if (_process is not null)
            {
                logger.LogWarning(
                    "Ignoring FFmpeg recording start because a recording is already active"
                );
                return;
            }

            var settings = settingsProvider.Current;
            var (wowProcess, windowHandle) = FindWowProcess();
            var captureSize = WowWindowCaptureSizeDetector.GetCaptureSize(windowHandle);
            var outputSize = ScreenRecordingService.CalculateOutputSize(settings, captureSize);
            var outputPath = ScreenRecordingService.CreateOutputPath(context, settings);
            var ffmpegPath = ResolveFfmpegPath();
            var startInfo = CreateStartInfo(
                ffmpegPath,
                windowHandle,
                settings,
                captureSize,
                outputSize,
                outputPath
            );

            ClearStderrTail();
            _recordingStarted = CreateCompletionSource();
            _recordingFinished = CreateCompletionSource();
            _isStopping = false;
            _outputPath = outputPath;
            _wowProcess = wowProcess;
            _recordingRequestedTimestamp = startRequestTimestamp;
            _recordingStartedTimestamp = 0;
            _stopRequestedTimestamp = 0;

            var process = new Process { StartInfo = startInfo };
            process.ErrorDataReceived += OnFfmpegErrorDataReceived;

            if (wowProcess is not null)
            {
                wowProcess.Exited += OnWowProcessExited;
                wowProcess.EnableRaisingEvents = true;
            }

            logger.LogInformation(
                "Starting FFmpeg recorder using {FfmpegPath}: window {WindowHandle}, capture {CaptureWidth}x{CaptureHeight}, output {OutputWidth}x{OutputHeight}, {FrameRate} FPS, cursor {CaptureCursor}, border {ShowCaptureBorder}, output {OutputPath}",
                ffmpegPath,
                windowHandle,
                captureSize.Width,
                captureSize.Height,
                outputSize.Width,
                outputSize.Height,
                settings.Video.FrameRate,
                settings.Video.CaptureCursor,
                settings.Video.ShowCaptureBorder,
                outputPath
            );

            if (!process.Start())
            {
                throw new InvalidOperationException("FFmpeg did not start.");
            }

            process.BeginErrorReadLine();
            _process = process;
            _ = MonitorProcessAsync(process);
            _ = ConfirmStartupAsync(process);
            recordingStarted = _recordingStarted.Task;
        }
        catch (Exception exception)
        {
            DisposeProcess();
            throw ClassifyStartException(exception);
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
        Process process;

        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            if (_process is null)
            {
                logger.LogDebug("Ignoring FFmpeg recording stop because no recording is active");
                return;
            }

            process = _process;
            recordingFinished = _recordingFinished.Task;

            if (!_isStopping)
            {
                _isStopping = true;
                _stopRequestedTimestamp = Stopwatch.GetTimestamp();
                logger.LogInformation(
                    "Requesting FFmpeg recording stop after {RecordingDuration}: {OutputPath}",
                    GetRecordingDuration(),
                    _outputPath
                );
                RequestGracefulStop(process);
            }
        }
        catch
        {
            DisposeProcess();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
            await recordingFinished.WaitAsync(cancellationToken);
        }
        finally
        {
            DisposeProcess();
        }
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

    private static ProcessStartInfo CreateStartInfo(
        string ffmpegPath,
        nint windowHandle,
        PullWatchSettings settings,
        VideoCaptureSize captureSize,
        VideoCaptureSize outputSize,
        string outputPath
    )
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(CreateFilter(windowHandle, settings, captureSize, outputSize));
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("h264_nvenc");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("p4");
        startInfo.ArgumentList.Add("-cq");
        startInfo.ArgumentList.Add(
            GetNvencCq(settings.Video.Quality).ToString(CultureInfo.InvariantCulture)
        );
        startInfo.ArgumentList.Add("-bf");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-surfaces");
        startInfo.ArgumentList.Add("8");
        startInfo.ArgumentList.Add(outputPath);
        return startInfo;
    }

    private static string CreateFilter(
        nint windowHandle,
        PullWatchSettings settings,
        VideoCaptureSize captureSize,
        VideoCaptureSize outputSize
    )
    {
        var filter = new StringBuilder();
        filter.Append("gfxcapture=");
        filter.Append("hwnd=");
        filter.Append(windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
        filter.Append(":capture_cursor=");
        filter.Append(ToFfmpegBoolean(settings.Video.CaptureCursor));
        filter.Append(":capture_border=0");
        filter.Append(":display_border=");
        filter.Append(ToFfmpegBoolean(settings.Video.ShowCaptureBorder));
        filter.Append(":max_framerate=");
        filter.Append(settings.Video.FrameRate.ToString(CultureInfo.InvariantCulture));

        if (outputSize != captureSize)
        {
            filter.Append(":width=");
            filter.Append(outputSize.Width.ToString(CultureInfo.InvariantCulture));
            filter.Append(":height=");
            filter.Append(outputSize.Height.ToString(CultureInfo.InvariantCulture));
            filter.Append(":resize_mode=scale_aspect");
        }

        filter.Append(",fps=");
        filter.Append(settings.Video.FrameRate.ToString(CultureInfo.InvariantCulture));
        return filter.ToString();
    }

    private static int GetNvencCq(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.Compact => 23,
            VideoQuality.Balanced => 20,
            VideoQuality.High => 17,
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
        };
    }

    private static string ToFfmpegBoolean(bool value)
    {
        return value ? "1" : "0";
    }

    private static string ResolveFfmpegPath()
    {
        return File.Exists(PreferredFfmpegPath) ? PreferredFfmpegPath : FfmpegExecutableName;
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

    private static Exception ClassifyStartException(Exception exception)
    {
        if (exception is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return new InvalidOperationException(
                "Screen recording cannot start because FFmpeg could not be found. Place ffmpeg.exe in C:\\ffmpeg\\bin, then restart PullWatch.",
                exception
            );
        }

        return RecordingFailureClassifier.Classify(exception);
    }

    private async Task ConfirmStartupAsync(Process process)
    {
        try
        {
            await Task.Delay(StartupConfirmationDelay);
            if (_process == process && !process.HasExited)
            {
                _recordingStartedTimestamp = Stopwatch.GetTimestamp();
                _recordingStarted.TrySetResult();
                logger.LogInformation(
                    "FFmpeg recorder process stayed alive for startup confirmation after {StartupDuration}",
                    GetElapsedTime(_recordingRequestedTimestamp)
                );
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "FFmpeg startup confirmation failed");
        }
    }

    private async Task MonitorProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            process.WaitForExit();

            var exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                _recordingStarted.TrySetResult();
                _recordingFinished.TrySetResult();
                logger.LogInformation(
                    "FFmpeg recorder exited with code 0; duration {RecordingDuration}, finalization {FinalizationDuration}, size {FileSizeMegabytes:F1} MB: {OutputPath}",
                    GetRecordingDuration(),
                    GetElapsedTime(_stopRequestedTimestamp),
                    GetFileSizeMegabytes(_outputPath),
                    _outputPath
                );

                if (!Volatile.Read(ref _isStopping))
                {
                    logger.LogInformation("FFmpeg recorder exited while a recording was active");
                    CaptureTargetExited?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            var failure = CreateFfmpegFailure(exitCode);
            logger.LogError(failure, "FFmpeg recorder exited with code {ExitCode}", exitCode);
            _recordingStarted.TrySetException(failure);
            _recordingFinished.TrySetException(failure);
            Failed?.Invoke(this, new RecordingServiceFailedEventArgs(failure));
            DisposeProcess();
        }
        catch (Exception exception)
        {
            var failure = new InvalidOperationException(
                "FFmpeg recorder monitoring failed.",
                exception
            );
            logger.LogError(failure, "FFmpeg recorder monitoring failed");
            _recordingStarted.TrySetException(failure);
            _recordingFinished.TrySetException(failure);
            Failed?.Invoke(this, new RecordingServiceFailedEventArgs(failure));
            DisposeProcess();
        }
    }

    private void RequestGracefulStop(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.StandardInput.WriteLine("q");
            process.StandardInput.Flush();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogDebug(exception, "FFmpeg process exited before the stop request was written");
        }
        catch (IOException exception)
        {
            logger.LogDebug(
                exception,
                "FFmpeg standard input closed before the stop request was written"
            );
        }
    }

    private void OnWowProcessExited(object? sender, EventArgs eventArgs)
    {
        logger.LogInformation("World of Warcraft exited while an FFmpeg recording was active");
        CaptureTargetExited?.Invoke(this, EventArgs.Empty);
    }

    private void OnFfmpegErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            return;
        }

        lock (_stderrLock)
        {
            _stderrTail.Enqueue(eventArgs.Data);
            while (_stderrTail.Count > StderrTailLimit)
            {
                _stderrTail.Dequeue();
            }
        }

        logger.LogInformation("FFmpeg: {FfmpegOutput}", eventArgs.Data);
    }

    private InvalidOperationException CreateFfmpegFailure(int exitCode)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"FFmpeg exited with code {exitCode}.");

        var tail = GetStderrTail();
        if (!string.IsNullOrWhiteSpace(tail))
        {
            message.AppendLine();
            message.AppendLine("Recent FFmpeg output:");
            message.Append(tail);
        }

        return new InvalidOperationException(message.ToString());
    }

    private string GetStderrTail()
    {
        lock (_stderrLock)
        {
            return string.Join(Environment.NewLine, _stderrTail);
        }
    }

    private void ClearStderrTail()
    {
        lock (_stderrLock)
        {
            _stderrTail.Clear();
        }
    }

    private void DisposeProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        var wowProcess = Interlocked.Exchange(ref _wowProcess, null);
        _isStopping = false;
        _outputPath = null;

        if (wowProcess is not null)
        {
            wowProcess.Exited -= OnWowProcessExited;
            wowProcess.Dispose();
        }

        if (process is not null)
        {
            process.ErrorDataReceived -= OnFfmpegErrorDataReceived;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(
                    exception,
                    "FFmpeg process cleanup could not kill the active process"
                );
            }

            process.Dispose();
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

    private static double GetFileSizeMegabytes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

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
