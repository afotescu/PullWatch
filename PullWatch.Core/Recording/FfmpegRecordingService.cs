using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

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
    private static readonly TimeSpan AudioPipeConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _stderrLock = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;
    private Process? _wowProcess;
    private FfmpegAudioPipe? _audioPipe;
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
            var audioPipe = CreateAudioPipe(settings);
            var startInfo = CreateStartInfo(
                ffmpegPath,
                windowHandle,
                settings,
                captureSize,
                outputSize,
                outputPath,
                audioPipe
            );

            ClearStderrTail();
            _recordingStarted = CreateCompletionSource();
            _recordingFinished = CreateCompletionSource();
            _isStopping = false;
            _outputPath = outputPath;
            _wowProcess = wowProcess;
            _audioPipe = audioPipe;
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

            LogAudioOptions(settings, audioPipe);

            if (!process.Start())
            {
                throw new InvalidOperationException("FFmpeg did not start.");
            }

            process.BeginErrorReadLine();
            _process = process;
            _ = MonitorProcessAsync(process);
            if (audioPipe is not null)
            {
                await audioPipe.ConnectAndStartAsync(AudioPipeConnectionTimeout, cancellationToken);
            }

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
                _audioPipe?.CloseInput();
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
        string outputPath,
        FfmpegAudioPipe? audioPipe
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

        if (audioPipe is not null)
        {
            startInfo.ArgumentList.Add("-thread_queue_size");
            startInfo.ArgumentList.Add("512");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(audioPipe.FfmpegFormat);
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add(audioPipe.SampleRate.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(audioPipe.Channels.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioPipe.PipePath);
        }

        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(
            $"{CreateFilter(windowHandle, settings, captureSize, outputSize)}[v]"
        );
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("[v]");
        if (audioPipe is not null)
        {
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0");
        }
        else
        {
            startInfo.ArgumentList.Add("-an");
        }

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
        if (audioPipe is not null)
        {
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("192k");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("48000");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("2");
        }

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

    private FfmpegAudioPipe? CreateAudioPipe(PullWatchSettings settings)
    {
        if (!settings.Audio.CaptureSystemAudio)
        {
            if (settings.Audio.CaptureMicrophone)
            {
                logger.LogWarning(
                    "FFmpeg experiment does not capture microphone-only audio yet; recording video without audio"
                );
            }

            return null;
        }

        if (settings.Audio.CaptureMicrophone)
        {
            logger.LogWarning(
                "FFmpeg experiment is capturing system audio only; microphone capture will be ignored for this recording"
            );
        }

        return FfmpegAudioPipe.CreateSystemAudio(logger);
    }

    private void LogAudioOptions(PullWatchSettings settings, FfmpegAudioPipe? audioPipe)
    {
        if (audioPipe is null)
        {
            logger.LogInformation(
                "FFmpeg audio disabled: system audio {CaptureSystemAudio}, microphone {CaptureMicrophone}",
                settings.Audio.CaptureSystemAudio,
                settings.Audio.CaptureMicrophone
            );
            return;
        }

        logger.LogInformation(
            "FFmpeg audio enabled: {AudioSource}, {AudioFormat}, {SampleRate} Hz, {Channels} channels",
            audioPipe.SourceDescription,
            audioPipe.FfmpegFormat,
            audioPipe.SampleRate,
            audioPipe.Channels
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
        var audioPipe = Interlocked.Exchange(ref _audioPipe, null);
        _isStopping = false;
        _outputPath = null;

        audioPipe?.Dispose();

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

    private sealed class FfmpegAudioPipe : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly ILogger _logger;
        private readonly NamedPipeServerStream _pipe;
        private readonly object _stateLock = new();
        private bool _disposed;
        private bool _stopping;

        private FfmpegAudioPipe(IWaveIn capture, ILogger logger, string sourceDescription)
        {
            _capture = capture;
            _logger = logger;
            SourceDescription = sourceDescription;
            var pipeName = $"PullWatchAudio-{Guid.NewGuid():N}";
            PipePath = @$"\\.\pipe\{pipeName}";
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous
            );

            FfmpegFormat = GetFfmpegFormat(capture.WaveFormat);
            SampleRate = capture.WaveFormat.SampleRate;
            Channels = capture.WaveFormat.Channels;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
        }

        public string SourceDescription { get; }

        public string PipePath { get; }

        public string FfmpegFormat { get; }

        public int SampleRate { get; }

        public int Channels { get; }

        public static FfmpegAudioPipe CreateSystemAudio(ILogger logger)
        {
            return new FfmpegAudioPipe(
                new WasapiLoopbackCapture(),
                logger,
                "default system loopback"
            );
        }

        public async Task ConnectAndStartAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken
        )
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutCancellation.CancelAfter(timeout);
            await _pipe.WaitForConnectionAsync(timeoutCancellation.Token);
            _capture.StartRecording();
        }

        public void CloseInput()
        {
            lock (_stateLock)
            {
                if (_disposed || _stopping)
                {
                    return;
                }

                _stopping = true;
            }

            try
            {
                _pipe.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "FFmpeg audio pipe close failed");
            }

            _ = Task.Run(StopCapture);
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "FFmpeg audio capture stop during disposal failed");
            }

            _capture.Dispose();
            _pipe.Dispose();
        }

        private void StopCapture()
        {
            try
            {
                _capture.StopRecording();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "FFmpeg audio capture stop failed");
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            if (eventArgs.BytesRecorded == 0 || !_pipe.IsConnected)
            {
                return;
            }

            try
            {
                _pipe.Write(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "FFmpeg audio pipe closed while writing audio");
            }
            catch (ObjectDisposedException)
            {
                // The recorder is shutting down.
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            if (eventArgs.Exception is not null)
            {
                _logger.LogWarning(
                    eventArgs.Exception,
                    "FFmpeg audio capture stopped unexpectedly"
                );
            }
        }

        private static string GetFfmpegFormat(WaveFormat format)
        {
            if (
                format.Encoding is WaveFormatEncoding.IeeeFloat
                || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
            )
            {
                return "f32le";
            }

            if (
                format.Encoding is WaveFormatEncoding.Pcm
                || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 16)
            )
            {
                return "s16le";
            }

            throw new InvalidOperationException(
                $"Unsupported system audio format for FFmpeg raw PCM pipe: {format.Encoding}, {format.BitsPerSample} bits."
            );
        }
    }
}
