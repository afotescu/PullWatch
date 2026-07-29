using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PullWatch;

internal static class FfmpegCalibrationProcessRunner
{
    private const int OutputTailLimit = 100;
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KillExitTimeout = TimeSpan.FromSeconds(3);

    public static async Task<FfmpegCalibrationProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string outputPath,
        TimeSpan requestedDuration,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        var startTimeUtc = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        var output = new FfmpegOutputAccumulator(outputPath);
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += output.OnStandardOutput;
        process.ErrorDataReceived += output.OnStandardError;

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(startInfo.FileName)} calibration process did not start."
            );
        }

        var processId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutFired = false;
        var callerCancellationFired = false;
        var gracefulShutdownAttempted = false;
        var qSent = false;
        var stdinClosed = false;
        var exitedAfterGracefulShutdown = false;
        var killEntireProcessTreeCalled = false;
        var exitedAfterKill = false;
        string? gracefulShutdownError = null;
        string? killError = null;

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );

        var exited = false;
        try
        {
            await process.WaitForExitAsync(combinedCancellation.Token);
            exited = true;
        }
        catch (OperationCanceledException)
        {
            callerCancellationFired = cancellationToken.IsCancellationRequested;
            timeoutFired = !callerCancellationFired && timeoutCancellation.IsCancellationRequested;
            output.ObserveOutputFile();

            if (timeoutFired)
            {
                if (process.HasExited)
                {
                    exited = true;
                }
                else
                {
                    gracefulShutdownAttempted = true;
                    try
                    {
                        if (startInfo.RedirectStandardInput && !process.HasExited)
                        {
                            await process.StandardInput.WriteLineAsync("q");
                            await process.StandardInput.FlushAsync();
                            qSent = true;
                            process.StandardInput.Close();
                            stdinClosed = true;
                        }
                    }
                    catch (Exception exception)
                        when (exception
                                is IOException
                                    or InvalidOperationException
                                    or ObjectDisposedException
                        )
                    {
                        gracefulShutdownError = exception.Message;
                    }

                    exited = await TryWaitForExitAsync(process, GracefulStopTimeout);
                    exitedAfterGracefulShutdown = exited;
                }
            }

            if (!exited)
            {
                try
                {
                    if (process.HasExited)
                    {
                        exited = true;
                    }
                    else
                    {
                        killEntireProcessTreeCalled = true;
                        process.Kill(entireProcessTree: true);
                        exited = await TryWaitForExitAsync(process, KillExitTimeout);
                        exitedAfterKill = exited;
                    }
                }
                catch (Exception exception)
                    when (exception
                            is Win32Exception
                                or InvalidOperationException
                                or NotSupportedException
                    )
                {
                    killError = exception.Message;
                }
            }
        }

        if (exited)
        {
            process.WaitForExit();
        }
        else
        {
            TryCancelRead(process.CancelOutputRead);
            TryCancelRead(process.CancelErrorRead);
        }

        output.ObserveOutputFile();
        var snapshot = output.GetSnapshot();
        int? exitCode = exited ? process.ExitCode : null;

        return new FfmpegCalibrationProcessResult(
            processId,
            startTimeUtc,
            exitCode,
            Stopwatch.GetElapsedTime(timestamp),
            timeoutFired,
            callerCancellationFired,
            gracefulShutdownAttempted,
            qSent,
            stdinClosed,
            exitedAfterGracefulShutdown,
            gracefulShutdownError,
            killEntireProcessTreeCalled,
            exitedAfterKill,
            killError,
            snapshot.StandardOutputTail,
            snapshot.StandardErrorTail,
            snapshot.LatestProgress,
            snapshot.LatestFrameCount,
            snapshot.LatestOutTime,
            snapshot.AnyFramesReceived,
            snapshot.OutputFileObserved,
            snapshot.MaximumObservedOutputFileSize,
            requestedDuration
        );
    }

    private static async Task<bool> TryWaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryCancelRead(Action cancelRead)
    {
        try
        {
            cancelRead();
        }
        catch (InvalidOperationException)
        {
            // The stream was already closed while process cleanup was running.
        }
    }

    private sealed class FfmpegOutputAccumulator(string outputPath)
    {
        private readonly object _lock = new();
        private readonly Queue<string> _standardOutputTail = new();
        private readonly Queue<string> _standardErrorTail = new();
        private readonly Dictionary<string, string> _progressValues = new(
            StringComparer.OrdinalIgnoreCase
        );
        private string? _latestProgress;
        private int _latestFrameCount;
        private TimeSpan _latestOutTime;
        private bool _outputFileObserved;
        private long _maximumObservedOutputFileSize;

        public void OnStandardOutput(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            lock (_lock)
            {
                Enqueue(_standardOutputTail, eventArgs.Data);
                ParseProgress(eventArgs.Data);
                ObserveOutputFileCore();
            }
        }

        public void OnStandardError(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            lock (_lock)
            {
                Enqueue(_standardErrorTail, eventArgs.Data);
            }
        }

        public void ObserveOutputFile()
        {
            lock (_lock)
            {
                ObserveOutputFileCore();
            }
        }

        public FfmpegOutputSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new FfmpegOutputSnapshot(
                    string.Join(Environment.NewLine, _standardOutputTail),
                    string.Join(Environment.NewLine, _standardErrorTail),
                    _latestProgress,
                    _latestFrameCount,
                    _latestOutTime,
                    _latestFrameCount > 0,
                    _outputFileObserved,
                    _maximumObservedOutputFileSize
                );
            }
        }

        private void ParseProgress(string line)
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                return;
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];
            _progressValues[key] = value;

            if (
                key.Equals("frame", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var frameCount
                )
            )
            {
                _latestFrameCount = Math.Max(_latestFrameCount, frameCount);
            }
            else if (
                key.Equals("out_time", StringComparison.OrdinalIgnoreCase)
                && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var outTime)
            )
            {
                _latestOutTime = outTime;
            }

            if (!key.Equals("progress", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _latestProgress = string.Join(
                "; ",
                new[] { "frame", "fps", "out_time", "total_size", "speed", "progress" }
                    .Where(_progressValues.ContainsKey)
                    .Select(progressKey => $"{progressKey}={_progressValues[progressKey]}")
            );
        }

        private void ObserveOutputFileCore()
        {
            try
            {
                var file = new FileInfo(outputPath);
                if (!file.Exists)
                {
                    return;
                }

                _outputFileObserved = true;
                _maximumObservedOutputFileSize = Math.Max(
                    _maximumObservedOutputFileSize,
                    file.Length
                );
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or UnauthorizedAccessException
                            or NotSupportedException
                )
            {
                // The final diagnostic snapshot will retry after process cleanup.
            }
        }

        private static void Enqueue(Queue<string> lines, string line)
        {
            lines.Enqueue(line.Trim());
            while (lines.Count > OutputTailLimit)
            {
                lines.Dequeue();
            }
        }
    }

    private sealed record FfmpegOutputSnapshot(
        string StandardOutputTail,
        string StandardErrorTail,
        string? LatestProgress,
        int LatestFrameCount,
        TimeSpan LatestOutTime,
        bool AnyFramesReceived,
        bool OutputFileObserved,
        long MaximumObservedOutputFileSize
    );
}

internal sealed record FfmpegCalibrationProcessResult(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    int? ExitCode,
    TimeSpan Elapsed,
    bool TimeoutFired,
    bool CallerCancellationFired,
    bool GracefulShutdownAttempted,
    bool QSent,
    bool StdinClosed,
    bool ExitedAfterGracefulShutdown,
    string? GracefulShutdownError,
    bool KillEntireProcessTreeCalled,
    bool ExitedAfterKill,
    string? KillError,
    string StandardOutputTail,
    string StandardErrorTail,
    string? LatestProgress,
    int LatestFrameCount,
    TimeSpan LatestOutTime,
    bool AnyFramesReceived,
    bool OutputFileObserved,
    long MaximumObservedOutputFileSize,
    TimeSpan RequestedDuration
)
{
    public bool ReachedRequestedDuration =>
        LatestOutTime >= RequestedDuration - TimeSpan.FromMilliseconds(100);
}
