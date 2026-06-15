using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class CombatLogReaderTests
{
    private const string FirstLine =
        "6/15/2026 00:15:10.0373  ENCOUNTER_START,3129,\"Plexus Sentinel\",14,10,2810";
    private const string SecondLine =
        "6/15/2026 00:16:10.0373  ENCOUNTER_END,3129,\"Plexus Sentinel\",14,10,1";

    [Fact]
    public async Task ExistingLogStartsAtEnd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-old.txt");
        await File.WriteAllTextAsync(path, FirstLine + Environment.NewLine, cancellationToken);
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = CreateReader(directory.Path);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token);
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
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = CreateReader(logsDirectory);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token);
        await WaitForStateAsync(reader, CombatLogReaderState.WaitingForLogsDirectory);
        Directory.CreateDirectory(logsDirectory);
        await WaitForStateAsync(reader, CombatLogReaderState.WaitingForCombatLog);
        await File.WriteAllTextAsync(
            Path.Combine(logsDirectory, "WoWCombatLog-new.txt"),
            FirstLine + Environment.NewLine,
            cancellationToken);
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
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            readerCancellation.Token);
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
    public async Task DoesNotEmitPartialLine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = CreateReader(directory.Path);

        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token);
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        var split = FirstLine.Length / 2;
        await File.AppendAllTextAsync(path, FirstLine[..split], cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.Empty(events);

        await File.AppendAllTextAsync(path, FirstLine[split..] + Environment.NewLine, cancellationToken);
        await WaitForCountAsync(events, 1);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(FirstLine, events.Single().RawLine);
    }

    [Fact]
    public async Task DoesNotRetryIOExceptionFromEventHandler()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WoWCombatLog-current.txt");
        await File.WriteAllTextAsync(path, "", cancellationToken);
        var reader = CreateReader(directory.Path);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var readTask = reader.ReadAsync(
            (_, _) => throw new IOException("Handler failed."),
            readerCancellation.Token);
        await WaitForStateAsync(reader, CombatLogReaderState.ReadingCombatLog);
        await File.AppendAllTextAsync(path, FirstLine + Environment.NewLine, cancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(() => readTask);
        Assert.Equal("Handler failed.", exception.Message);
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
            FileShare.None);
        var events = new ConcurrentQueue<CombatLogEvent>();
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = CreateReader(directory.Path);
        var readTask = reader.ReadAsync(
            (combatLogEvent, _) =>
            {
                events.Enqueue(combatLogEvent);
                return Task.CompletedTask;
            },
            readerCancellation.Token);

        await WaitForAsync(() => reader.Status.LastFileSystemError is not null);
        await exclusiveLock.DisposeAsync();
        await File.AppendAllTextAsync(path, FirstLine + Environment.NewLine, cancellationToken);
        await WaitForCountAsync(events, 1);

        Assert.Null(reader.Status.LastFileSystemError);

        readerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private static CombatLogReader CreateReader(string logsDirectory)
    {
        return new CombatLogReader(
            logsDirectory,
            NullLogger<CombatLogReader>.Instance,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(40));
    }

    private static async Task WaitForStateAsync(
        CombatLogReader reader,
        CombatLogReaderState expectedState)
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
        int expectedCount)
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
