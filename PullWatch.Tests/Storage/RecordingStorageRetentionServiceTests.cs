using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class RecordingStorageRetentionServiceTests
{
    [Fact]
    public async Task UsageCountsOnlyManagedAvailableRecordingsInActiveDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var service = new RecordingStorageRetentionService(
            database.Catalog,
            NullLogger<RecordingStorageRetentionService>.Instance
        );
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var outsideDirectory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Outside")
        );
        var managedPath = WriteFile(directory.FullName, "managed.mp4", 40);
        WriteFile(directory.FullName, "loose.mp4", 100);
        var activePath = WriteFile(directory.FullName, "active.mp4", 100);
        var outsidePath = WriteFile(outsideDirectory.FullName, "outside.mp4", 100);
        await database.Repository.UpsertAsync(
            CreateSave(Guid.Parse("93C66F94-CFE2-4545-8647-9F35284C84A7"), managedPath),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(Guid.Parse("06A73970-107D-411A-AD84-35154193B1F5"), activePath) with
            {
                Status = RecordingCatalogStatus.Recording,
            },
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(Guid.Parse("9145106E-AF8D-4A7C-BD50-DF7175755C30"), outsidePath),
            cancellationToken
        );

        var usage = await service.GetUsageAsync(Settings(directory.FullName), cancellationToken);

        Assert.Equal(40, usage.UsageBytes);
        Assert.Equal(1, usage.RecordingCount);
    }

    [Fact]
    public async Task EnforceLimitDeletesOldestManagedRecordingsUntilBelowTarget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var logger = new CapturingLogger<RecordingStorageRetentionService>();
        var service = new RecordingStorageRetentionService(database.Catalog, logger);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var oldestId = Guid.Parse("C8E54271-F58D-43C2-88BD-BD4E6EF123E6");
        var middleId = Guid.Parse("172D7EA0-2EA3-483F-9D2B-AEFC87CB84BA");
        var newestId = Guid.Parse("8FF424B2-9775-43EB-A549-0935D98F5772");
        var oldestPath = WriteFile(directory.FullName, "oldest.mp4", 40);
        var middlePath = WriteFile(directory.FullName, "middle.mp4", 40);
        var newestPath = WriteFile(directory.FullName, "newest.mp4", 40);
        await database.Repository.UpsertAsync(
            CreateSave(oldestId, oldestPath, DateTimeOffset.Parse("2026-06-20T10:00:00Z")),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(middleId, middlePath, DateTimeOffset.Parse("2026-06-20T11:00:00Z")),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(newestId, newestPath, DateTimeOffset.Parse("2026-06-20T12:00:00Z")),
            cancellationToken
        );

        var result = await service.EnforceLimitAsync(
            Settings(directory.FullName, maxUsageBytes: 100),
            cancellationToken
        );

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(80, result.Usage.UsageBytes);
        Assert.Equal(2, result.Usage.RecordingCount);
        Assert.False(File.Exists(oldestPath));
        Assert.True(File.Exists(middlePath));
        Assert.True(File.Exists(newestPath));
        Assert.Null(await database.Repository.GetByIdAsync(oldestId, cancellationToken));
        Assert.NotNull(await database.Repository.GetByIdAsync(middleId, cancellationToken));
        Assert.NotNull(await database.Repository.GetByIdAsync(newestId, cancellationToken));
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Debug
                && entry.Message.Contains(oldestId.ToString(), StringComparison.Ordinal)
                && entry.Message.Contains(oldestPath, StringComparison.Ordinal)
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains(
                    "Deleted 1 old managed recordings",
                    StringComparison.Ordinal
                )
                && entry.Message.Contains("80 of 100 bytes", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task UnlimitedLimitDoesNotDeleteManagedRecordings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var service = new RecordingStorageRetentionService(
            database.Catalog,
            NullLogger<RecordingStorageRetentionService>.Instance
        );
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var id = Guid.Parse("2A7720F4-A848-4717-8D00-1D967FB83E20");
        var path = WriteFile(directory.FullName, "recording.mp4", 120);
        await database.Repository.UpsertAsync(CreateSave(id, path), cancellationToken);

        var result = await service.EnforceLimitAsync(
            Settings(directory.FullName, RecordingStorageSettings.UnlimitedBytes),
            cancellationToken
        );

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(120, result.Usage.UsageBytes);
        Assert.True(File.Exists(path));
        Assert.NotNull(await database.Repository.GetByIdAsync(id, cancellationToken));
    }

    private static PullWatchSettings Settings(
        string recordingsDirectory,
        long maxUsageBytes = RecordingStorageSettings.DefaultMaxUsageBytes
    )
    {
        return new PullWatchSettings
        {
            RecordingsDirectory = recordingsDirectory,
            Storage = new RecordingStorageSettings { MaxUsageBytes = maxUsageBytes },
        };
    }

    private static RecordingCatalogSave CreateSave(
        Guid id,
        string filePath,
        DateTimeOffset? startedAt = null
    )
    {
        var recordingStartedAt = startedAt ?? DateTimeOffset.Parse("2026-06-20T10:00:00Z");

        return new RecordingCatalogSave(
            id,
            filePath,
            RecordingCatalogStatus.Available,
            RecordingCatalogKind.Manual,
            recordingStartedAt,
            recordingStartedAt.AddMinutes(5),
            null,
            recordingStartedAt.AddMinutes(5)
        );
    }

    private static string WriteFile(string directory, string fileName, int sizeBytes)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    private sealed class TemporaryRecordingDatabase : IDisposable
    {
        private readonly TemporaryDirectory _directory;

        private TemporaryRecordingDatabase(
            TemporaryDirectory directory,
            SqliteConnectionFactory connectionFactory
        )
        {
            _directory = directory;
            Repository = new RecordingCatalogRepository(connectionFactory);
            Catalog = new RecordingCatalog(Repository);
        }

        public string DirectoryPath => _directory.Path;

        public RecordingCatalogRepository Repository { get; }

        public RecordingCatalog Catalog { get; }

        public static async Task<TemporaryRecordingDatabase> CreateAsync(
            CancellationToken cancellationToken
        )
        {
            var directory = new TemporaryDirectory();
            var databasePath = Path.Combine(directory.Path, "pullwatch.db");
            var factory = new SqliteConnectionFactory(
                new RecordingDatabasePathProvider(databasePath)
            );
            var initializer = new RecordingStorageInitializer(factory, NullLoggerFactory.Instance);

            try
            {
                await initializer.InitializeAsync(cancellationToken);
                return new TemporaryRecordingDatabase(directory, factory);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchRetentionTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, string Message);
}
