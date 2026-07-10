using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using PullWatch.Tests.TestDoubles;

namespace PullWatch.Tests;

public sealed class CombatLogReaderTests
{
    private static readonly string FirstLine =
        $"6/15/2026 00:15:10.0373  ENCOUNTER_START,3129,\"Plexus Sentinel\",{WowDifficultyIds.NormalRaid},10,2810";
    private static readonly string SecondLine =
        $"6/15/2026 00:16:10.0373  ENCOUNTER_END,3129,\"Plexus Sentinel\",{WowDifficultyIds.NormalRaid},10,1";
    private const string MalformedChallengeStartLine =
        "6/15/2026 00:17:10.0373  CHALLENGE_MODE_START,\"Magisters' Terrace\",2811,558";
    private const string ValidChallengeStartLine =
        "6/15/2026 00:18:10.0373  CHALLENGE_MODE_START,\"Magisters' Terrace\",2811,558,22,[9,10,147]";

    [Fact]
    public async Task ExistingLogStartsAtEnd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-old.txt");
        await File.WriteAllTextAsync(path, FirstLine + Environment.NewLine, cancellationToken);
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        await File.AppendAllTextAsync(path, SecondLine + Environment.NewLine, cancellationToken);
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(WowEvents.EncounterEnd, events.Single().Name);
        Assert.NotNull(reader.Status.LastSuccessfulReadTime);
    }

    [Fact]
    public async Task CreatedDirectoryAndLogAreReadFromBeginning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var parent = new TemporaryDirectory();
        var logsDirectory = Path.Combine(parent.Path, "Logs");
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(logsDirectory);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.WaitingForLogsDirectory);
        Directory.CreateDirectory(logsDirectory);
        await WaitForStateAsync(reader, CombatLogReaderState.WaitingForCombatLog);
        await File.WriteAllTextAsync(
            Path.Combine(logsDirectory, "WoWCombatLog-new.txt"),
            FirstLine + Environment.NewLine,
            cancellationToken
        );
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(WowEvents.EncounterStart, events.Single().Name);
    }

    [Fact]
    public async Task SwitchesToNewerLogAndReadsItFromBeginning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "WoWCombatLog-old.txt");
        await File.WriteAllTextAsync(oldPath, FirstLine + Environment.NewLine, cancellationToken);
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-2));
        var events = new ConcurrentQueue<CombatLogEvent>();
        var switchingCount = 0;
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path);
        reader.StatusChanged += status =>
        {
            if (status.State == CombatLogReaderState.SwitchingCombatLog)
            {
                Interlocked.Increment(ref switchingCount);
            }
        };

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        var newPath = Path.Combine(directory.Path, "WoWCombatLog-new.txt");
        await File.WriteAllTextAsync(newPath, SecondLine + Environment.NewLine, cancellationToken);
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(WowEvents.EncounterEnd, events.Single().Name);
        Assert.Equal(1, switchingCount);
    }

    [Fact]
    public async Task IgnoresLogsCreatedBeforeWowSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var processStartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var stalePath = Path.Combine(directory.Path, "WoWCombatLog-stale.txt");
        await File.WriteAllTextAsync(stalePath, FirstLine + Environment.NewLine, cancellationToken);
        File.SetCreationTimeUtc(stalePath, processStartedAtUtc.AddMinutes(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow);
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path, wowProcessStartedAtUtc: processStartedAtUtc);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.WaitingForCombatLog);

        var currentPath = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(
            currentPath,
            SecondLine + Environment.NewLine,
            cancellationToken
        );
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(currentPath, reader.Status.CurrentPath);
        Assert.Equal(WowEvents.EncounterEnd, events.Single().Name);
    }

    [Fact]
    public async Task KeepsSelectedLogForKnownWowSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var processStartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var currentPath = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(currentPath, "", cancellationToken);
        File.SetCreationTimeUtc(currentPath, processStartedAtUtc.AddSeconds(1).UtcDateTime);
        File.SetLastWriteTimeUtc(currentPath, DateTime.UtcNow.AddMinutes(-2));
        var events = new ConcurrentQueue<CombatLogEvent>();
        var switchingCount = 0;
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path, wowProcessStartedAtUtc: processStartedAtUtc);
        reader.StatusChanged += status =>
        {
            if (status.State == CombatLogReaderState.SwitchingCombatLog)
            {
                Interlocked.Increment(ref switchingCount);
            }
        };

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);

        var newerPath = Path.Combine(directory.Path, "WoWCombatLog-new.txt");
        await File.WriteAllTextAsync(newerPath, FirstLine + Environment.NewLine, cancellationToken);
        File.SetCreationTimeUtc(newerPath, processStartedAtUtc.AddSeconds(2).UtcDateTime);
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);
        await Task.Delay(150, cancellationToken);

        Assert.Equal(currentPath, reader.Status.CurrentPath);
        Assert.Empty(events);
        Assert.Equal(0, switchingCount);

        await File.AppendAllTextAsync(
            currentPath,
            SecondLine + Environment.NewLine,
            cancellationToken
        );
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(currentPath, reader.Status.CurrentPath);
        Assert.Equal(SecondLine, events.Single().RawLine);
        Assert.Equal(0, switchingCount);
    }

    [Fact]
    public async Task DoesNotRediscoverNewerLogBeforeDiscoveryInterval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var currentPath = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(currentPath, "", cancellationToken);
        File.SetLastWriteTimeUtc(currentPath, DateTime.UtcNow.AddMinutes(-2));
        var events = new ConcurrentQueue<CombatLogEvent>();
        var switchingCount = 0;
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path, discoveryInterval: TimeSpan.FromSeconds(30));
        reader.StatusChanged += status =>
        {
            if (status.State == CombatLogReaderState.SwitchingCombatLog)
            {
                Interlocked.Increment(ref switchingCount);
            }
        };

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);

        var newerPath = Path.Combine(directory.Path, "WoWCombatLog-new.txt");
        await File.WriteAllTextAsync(newerPath, FirstLine + Environment.NewLine, cancellationToken);
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);
        await Task.Delay(150, cancellationToken);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Empty(events);
        Assert.Equal(0, switchingCount);
        Assert.Equal(currentPath, reader.Status.CurrentPath);
    }

    [Fact]
    public async Task DoesNotDiscoverNewLogWithoutSessionWhileDiscoveryIsPaused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var events = new ConcurrentQueue<CombatLogEvent>();
        var canDiscoverCombatLog = 1;
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(
            directory.Path,
            () => Volatile.Read(ref canDiscoverCombatLog) == 1
        );

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.WaitingForCombatLog);

        Volatile.Write(ref canDiscoverCombatLog, 0);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "WoWCombatLog-new.txt"),
            FirstLine + Environment.NewLine,
            cancellationToken
        );
        await Task.Delay(150, cancellationToken);

        Assert.Empty(events);
        Assert.Equal(CombatLogReaderState.WaitingForCombatLog, reader.Status.State);

        Volatile.Write(ref canDiscoverCombatLog, 1);
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(WowEvents.EncounterStart, events.Single().Name);
    }

    [Fact]
    public async Task KeepsReadingCurrentLogAndDoesNotSwitchWhileDiscoveryIsPaused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var currentPath = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(currentPath, "", cancellationToken);
        File.SetLastWriteTimeUtc(currentPath, DateTime.UtcNow.AddMinutes(-2));
        var events = new ConcurrentQueue<CombatLogEvent>();
        var canDiscoverCombatLog = 1;
        var switchingCount = 0;
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(
            directory.Path,
            () => Volatile.Read(ref canDiscoverCombatLog) == 1
        );
        reader.StatusChanged += status =>
        {
            if (status.State == CombatLogReaderState.SwitchingCombatLog)
            {
                Interlocked.Increment(ref switchingCount);
            }
        };

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);

        Volatile.Write(ref canDiscoverCombatLog, 0);
        var newerPath = Path.Combine(directory.Path, "WoWCombatLog-new.txt");
        await File.WriteAllTextAsync(newerPath, FirstLine + Environment.NewLine, cancellationToken);
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);
        await Task.Delay(150, cancellationToken);

        Assert.Equal(currentPath, reader.Status.CurrentPath);
        Assert.Equal(0, switchingCount);

        await File.AppendAllTextAsync(
            currentPath,
            SecondLine + Environment.NewLine,
            cancellationToken
        );
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(SecondLine, events.Single().RawLine);
        Assert.Equal(0, switchingCount);
    }

    [Fact]
    public async Task DoesNotEmitPartialLine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        var split = FirstLine.Length / 2;
        await File.AppendAllTextAsync(path, FirstLine[..split], cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.Empty(events);

        await File.AppendAllTextAsync(
            path,
            FirstLine[split..] + Environment.NewLine,
            cancellationToken
        );
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(FirstLine, events.Single().RawLine);
    }

    [Fact]
    public async Task CoalescesSuccessfulReadStatusDuringBurst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        const int eventCount = 500;
        var processedEventCount = 0;
        var successfulReadStatuses = new ConcurrentQueue<CombatLogReaderStatus>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(
            directory.Path,
            successfulReadStatusInterval: TimeSpan.FromMinutes(1)
        );
        reader.StatusChanged += status =>
        {
            if (status.LastSuccessfulReadTime is not null)
            {
                successfulReadStatuses.Enqueue(status);
            }
        };

        var readTask = reader.ReadAsync(
            (_, _) =>
            {
                Interlocked.Increment(ref processedEventCount);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        var burst = string.Concat(Enumerable.Repeat(FirstLine + Environment.NewLine, eventCount));
        await File.AppendAllTextAsync(path, burst, cancellationToken);
        await WaitForAsync(() => Volatile.Read(ref processedEventCount) == eventCount);

        Assert.Single(successfulReadStatuses);
        Assert.NotNull(reader.Status.LastSuccessfulReadTime);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task DoesNotRetryIOExceptionFromEventHandler()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        var reader = CreateReader(directory.Path);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );

        var readTask = reader.ReadAsync(
            (_, _) => throw new IOException("Handler failed."),
            readerCancellation.Token
        );
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        await File.AppendAllTextAsync(path, FirstLine + Environment.NewLine, cancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(() => readTask);
        Assert.Equal("Handler failed.", exception.Message);
    }

    [Fact]
    public async Task MalformedKnownEventDoesNotStopReader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        var recorder = new FakeRecordingService();
        var handler = CreateHandler(recorder);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path);

        var readTask = reader.ReadAsync(handler.HandleAsync, readerCancellation.Token);
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        await File.AppendAllTextAsync(
            path,
            MalformedChallengeStartLine
                + Environment.NewLine
                + ValidChallengeStartLine
                + Environment.NewLine,
            cancellationToken
        );
        await WaitForAsync(() => recorder.Calls.Count >= 1 || readTask.IsCompleted);

        Assert.Equal(["start"], recorder.Calls);
        Assert.False(readTask.IsCompleted, readTask.Exception?.ToString());

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task SuccessfulReadClearsTransientFileSystemError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        await using var exclusiveLock = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path);
        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token
        );

        await WaitForAsync(() => reader.Status.LastFileSystemError is not null);
        await exclusiveLock.DisposeAsync();
        await File.AppendAllTextAsync(path, FirstLine + Environment.NewLine, cancellationToken);
        await WaitForCountAsync(events, 1);

        Assert.Null(reader.Status.LastFileSystemError);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task ReopenedLogClearsTransientFileSystemErrorWithoutNewBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        await using var exclusiveLock = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var reader = CreateReader(directory.Path);
        var readTask = reader.ReadAsync((_, _) => Task.CompletedTask, readerCancellation.Token);

        await WaitForAsync(() => reader.Status.LastFileSystemError is not null);
        await exclusiveLock.DisposeAsync();
        await WaitForAsync(() =>
            reader.Status.State == CombatLogReaderState.ReadingCombatLog
            && reader.Status.LastFileSystemError is null
        );

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private static CombatLogReader CreateReader(
        string logsDirectory,
        Func<bool>? canDiscoverCombatLog = null,
        TimeSpan? discoveryInterval = null,
        DateTimeOffset? wowProcessStartedAtUtc = null,
        TimeSpan? successfulReadStatusInterval = null
    )
    {
        return new CombatLogReader(
            logsDirectory,
            NullLogger<CombatLogReader>.Instance,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(40),
            canDiscoverCombatLog,
            discoveryInterval ?? TimeSpan.FromMilliseconds(10),
            wowProcessStartedAtUtc,
            successfulReadStatusInterval
        );
    }

    private static CombatLogEventHandler CreateHandler(IRecordingService recordingService)
    {
        var coordinator = new RecordingCoordinator(
            recordingService,
            NullLogger<RecordingCoordinator>.Instance
        );

        return new CombatLogEventHandler(
            coordinator,
            new SettingsProvider(new PullWatchSettings()),
            NullLogger<CombatLogEventHandler>.Instance
        );
    }

    private static async Task WaitForStateAsync(
        CombatLogReader reader,
        CombatLogReaderState expectedState
    )
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (reader.Status.State != expectedState)
        {
            Assert.True(DateTime.UtcNow < timeout, $"Reader did not reach state {expectedState}.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitForCountAsync(
        ConcurrentQueue<CombatLogEvent> events,
        int expectedCount
    )
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (events.Count < expectedCount)
        {
            Assert.True(DateTime.UtcNow < timeout, $"Reader did not emit {expectedCount} events.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchCombatLogReaderTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
