using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class CombatLogReader : ICombatLogMonitor
{
    private const string CombatLogPattern = "WoWCombatLog-*";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultDiscoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultMaximumRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(30);

    private readonly string _logsDirectory;
    private readonly ILogger<CombatLogReader> _logger;
    private readonly Func<bool> _canDiscoverCombatLog;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _discoveryInterval;
    private readonly TimeSpan _maximumRetryDelay;
    private CombatLogReaderStatus _status = new(
        CombatLogReaderState.WaitingForLogsDirectory,
        null,
        null,
        null
    );
    private DateTimeOffset _lastErrorLogTime = DateTimeOffset.MinValue;
    private string? _lastLoggedErrorMessage;
    private long _lastDiscoveryTimestamp;
    private bool _hasPublishedState;

    public CombatLogReader(
        string logsDirectory,
        ILogger<CombatLogReader> logger,
        TimeSpan? pollInterval = null,
        TimeSpan? maximumRetryDelay = null,
        Func<bool>? canDiscoverCombatLog = null,
        TimeSpan? discoveryInterval = null
    )
    {
        _logsDirectory = logsDirectory;
        _logger = logger;
        _canDiscoverCombatLog = canDiscoverCombatLog ?? (() => true);
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _discoveryInterval = discoveryInterval ?? DefaultDiscoveryInterval;
        _maximumRetryDelay = maximumRetryDelay ?? DefaultMaximumRetryDelay;
    }

    public event Action<CombatLogReaderStatus>? StatusChanged;

    public CombatLogReaderStatus Status => Volatile.Read(ref _status);

    public async Task ReadAsync(
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(handleEventAsync);

        var initialCandidate = CanDiscoverCombatLog() ? DiscoverLatestCombatLog().Candidate : null;
        FileSession? session = initialCandidate is null
            ? null
            : new FileSession(initialCandidate, initialCandidate.Length, []);
        var retryDelay = _pollInterval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session is null)
            {
                if (!CanDiscoverCombatLog() || !ShouldDiscoverCombatLog())
                {
                    await Task.Delay(_pollInterval, cancellationToken);
                    continue;
                }

                var discovery = DiscoverLatestCombatLog();
                PublishWaitingStatus(discovery);

                if (discovery.Candidate is null)
                {
                    await Task.Delay(_pollInterval, cancellationToken);
                    continue;
                }

                session = new FileSession(discovery.Candidate, 0, []);
                retryDelay = _pollInterval;
            }

            try
            {
                session = await ReadSessionAsync(session, handleEventAsync, cancellationToken);
                retryDelay = _pollInterval;
            }
            catch (CombatLogFileSystemException exception)
            {
                PublishError(exception.FileSystemError, session!.Candidate.FullName);
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = IncreaseRetryDelay(retryDelay);
            }
        }
    }

    private async Task<FileSession?> ReadSessionAsync(
        FileSession session,
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken
    )
    {
        await using var stream = OpenSessionStream(session);
        PublishReadableStatus(session.Candidate.FullName);

        var buffer = new byte[4096];

        while (true)
        {
            var bytesRead = await ReadBytesAsync(stream, buffer, cancellationToken);

            if (bytesRead > 0)
            {
                session.Offset = stream.Position;
                PublishSuccessfulRead(session.Candidate.FullName);
                await ProcessBytesAsync(
                    buffer.AsMemory(0, bytesRead),
                    session.PendingLine,
                    handleEventAsync,
                    cancellationToken
                );
                continue;
            }

            if (CanDiscoverCombatLog() && ShouldDiscoverCombatLog())
            {
                var discovery = DiscoverLatestCombatLog();

                if (discovery.Error is not null)
                {
                    PublishError(discovery.Error, session.Candidate.FullName);
                }
                else if (!File.Exists(session.Candidate.FullName))
                {
                    PublishWaitingStatus(discovery);
                    return discovery.Candidate is null
                        ? null
                        : SwitchTo(discovery.Candidate, session.Candidate.FullName);
                }
                else if (
                    discovery.Candidate is not null
                    && !PathComparer.Equals(
                        discovery.Candidate.FullName,
                        session.Candidate.FullName
                    )
                    && discovery.Candidate.LastWriteTimeUtc > session.Candidate.LastWriteTimeUtc
                )
                {
                    return SwitchTo(discovery.Candidate, session.Candidate.FullName);
                }
            }
            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private static FileStream OpenSessionStream(FileSession session)
    {
        FileStream? stream = null;

        try
        {
            stream = new FileStream(
                session.Candidate.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );

            stream.Seek(session.Offset, SeekOrigin.Begin);
            return stream;
        }
        catch (Exception exception) when (IsTransientFileSystemException(exception))
        {
            stream?.Dispose();
            throw new CombatLogFileSystemException(exception);
        }
    }

    private static async Task<int> ReadBytesAsync(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception exception) when (IsTransientFileSystemException(exception))
        {
            throw new CombatLogFileSystemException(exception);
        }
    }

    private async Task ProcessBytesAsync(
        ReadOnlyMemory<byte> bytes,
        List<byte> pendingLine,
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken
    )
    {
        var completedLines = ExtractCompletedLines(bytes.Span, pendingLine);

        foreach (var line in completedLines)
        {
            _logger.LogDebug("{CombatLogLine}", line);

            if (CombatLogParser.TryParseEvent(line, out var combatLogEvent))
            {
                await handleEventAsync(combatLogEvent, cancellationToken);
            }
        }
    }

    private static List<string> ExtractCompletedLines(
        ReadOnlySpan<byte> bytes,
        List<byte> pendingLine
    )
    {
        var completedLines = new List<string>();

        foreach (var value in bytes)
        {
            if (value != (byte)'\n')
            {
                pendingLine.Add(value);
                continue;
            }

            if (pendingLine.Count > 0 && pendingLine[^1] == (byte)'\r')
            {
                pendingLine.RemoveAt(pendingLine.Count - 1);
            }

            var line = Encoding.UTF8.GetString(pendingLine.ToArray());
            pendingLine.Clear();
            completedLines.Add(line);
        }

        return completedLines;
    }

    private bool CanDiscoverCombatLog()
    {
        return _canDiscoverCombatLog();
    }

    private bool ShouldDiscoverCombatLog()
    {
        var lastDiscoveryTimestamp = Volatile.Read(ref _lastDiscoveryTimestamp);
        return lastDiscoveryTimestamp == 0
            || Stopwatch.GetElapsedTime(lastDiscoveryTimestamp) >= _discoveryInterval;
    }

    private DiscoveryResult DiscoverLatestCombatLog()
    {
        Volatile.Write(ref _lastDiscoveryTimestamp, Stopwatch.GetTimestamp());

        try
        {
            FileInfo? latestCombatLog = null;

            foreach (var file in new DirectoryInfo(_logsDirectory).EnumerateFiles(CombatLogPattern))
            {
                if (
                    latestCombatLog is null
                    || file.LastWriteTimeUtc > latestCombatLog.LastWriteTimeUtc
                )
                {
                    latestCombatLog = file;
                }
            }

            if (latestCombatLog is null)
            {
                return new DiscoveryResult(true, null, null);
            }

            return new DiscoveryResult(
                true,
                new FileCandidate(
                    latestCombatLog.FullName,
                    latestCombatLog.LastWriteTimeUtc,
                    latestCombatLog.Length
                ),
                null
            );
        }
        catch (DirectoryNotFoundException)
        {
            return new DiscoveryResult(false, null, null);
        }
        catch (Exception exception) when (IsTransientFileSystemException(exception))
        {
            return new DiscoveryResult(true, null, exception);
        }
    }

    private FileSession SwitchTo(FileCandidate candidate, string currentPath)
    {
        PublishStatus(CombatLogReaderState.SwitchingCombatLog, currentPath);
        return new FileSession(candidate, 0, []);
    }

    private void PublishWaitingStatus(DiscoveryResult discovery)
    {
        if (discovery.Error is not null)
        {
            PublishError(discovery.Error, null);
            return;
        }

        var state = !discovery.LogsDirectoryExists
            ? CombatLogReaderState.WaitingForLogsDirectory
            : CombatLogReaderState.WaitingForCombatLog;
        PublishStatus(state, null);
    }

    private void PublishSuccessfulRead(string path)
    {
        var current = Status;
        SetStatus(
            current with
            {
                State = CombatLogReaderState.ReadingCombatLog,
                CurrentPath = path,
                LastSuccessfulReadTime = DateTimeOffset.UtcNow,
                LastFileSystemError = null,
            }
        );
    }

    private void PublishReadableStatus(string path)
    {
        var current = Status;
        if (
            _hasPublishedState
            && current.State == CombatLogReaderState.ReadingCombatLog
            && PathComparer.Equals(current.CurrentPath, path)
            && current.LastFileSystemError is null
        )
        {
            return;
        }

        _hasPublishedState = true;
        SetStatus(
            current with
            {
                State = CombatLogReaderState.ReadingCombatLog,
                CurrentPath = path,
                LastFileSystemError = null,
            }
        );

        _logger.LogInformation(
            "Combat log reader state changed to {CombatLogReaderState}; path {CombatLogPath}",
            CombatLogReaderState.ReadingCombatLog,
            path
        );
    }

    private void PublishError(Exception exception, string? currentPath)
    {
        var current = Status;
        SetStatus(current with { CurrentPath = currentPath, LastFileSystemError = exception });

        var now = DateTimeOffset.UtcNow;
        if (
            !StringComparer.Ordinal.Equals(_lastLoggedErrorMessage, exception.Message)
            || now - _lastErrorLogTime >= ErrorLogInterval
        )
        {
            _logger.LogWarning(exception, "Combat log filesystem operation failed; retrying");
            _lastLoggedErrorMessage = exception.Message;
            _lastErrorLogTime = now;
        }
    }

    private void PublishStatus(CombatLogReaderState state, string? currentPath)
    {
        var current = Status;
        if (
            _hasPublishedState
            && current.State == state
            && PathComparer.Equals(current.CurrentPath, currentPath)
        )
        {
            return;
        }

        _hasPublishedState = true;
        SetStatus(current with { State = state, CurrentPath = currentPath });

        if (
            state
            is CombatLogReaderState.WaitingForLogsDirectory
                or CombatLogReaderState.WaitingForCombatLog
        )
        {
            _logger.LogWarning(
                "Combat log reader state changed to {CombatLogReaderState}; path {CombatLogPath}",
                state,
                currentPath
            );
        }
        else
        {
            _logger.LogInformation(
                "Combat log reader state changed to {CombatLogReaderState}; path {CombatLogPath}",
                state,
                currentPath
            );
        }
    }

    private void SetStatus(CombatLogReaderStatus status)
    {
        Volatile.Write(ref _status, status);

        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<CombatLogReaderStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(status);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Combat log reader status subscriber failed");
            }
        }
    }

    private TimeSpan IncreaseRetryDelay(TimeSpan current)
    {
        return TimeSpan.FromMilliseconds(
            Math.Min(current.TotalMilliseconds * 2, _maximumRetryDelay.TotalMilliseconds)
        );
    }

    private static bool IsTransientFileSystemException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    private static StringComparer PathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    private sealed record FileCandidate(string FullName, DateTime LastWriteTimeUtc, long Length);

    private sealed class FileSession(FileCandidate candidate, long offset, List<byte> pendingLine)
    {
        public FileCandidate Candidate { get; } = candidate;

        public long Offset { get; set; } = offset;

        public List<byte> PendingLine { get; } = pendingLine;
    }

    private sealed record DiscoveryResult(
        bool LogsDirectoryExists,
        FileCandidate? Candidate,
        Exception? Error
    );

    private sealed class CombatLogFileSystemException(Exception fileSystemError) : Exception
    {
        public Exception FileSystemError { get; } = fileSystemError;
    }
}
