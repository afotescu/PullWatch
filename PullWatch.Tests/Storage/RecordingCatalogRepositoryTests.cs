using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class RecordingCatalogRepositoryTests
{
    [Fact]
    public async Task UpsertAndReadRoundTripRecordingWithUtcStorageConventions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var repository = new RecordingCatalogRepository(database.ConnectionFactory);
        var id = Guid.Parse("4C016046-A2BD-4D96-9C49-B469A7373364");
        var startedAt = new DateTimeOffset(2026, 6, 20, 17, 3, 22, TimeSpan.FromHours(3)).AddTicks(
            1234567
        );
        var fileModifiedAt = startedAt.AddMinutes(5);
        var recording = new RecordingCatalogSave(
            id,
            @"D:\Recordings\manual.mp4",
            RecordingCatalogStatus.Available,
            RecordingCatalogKind.Manual,
            startedAt,
            null,
            1024,
            fileModifiedAt
        );

        await repository.UpsertAsync(recording, cancellationToken);

        var loaded = await repository.GetByIdAsync(id, cancellationToken);
        var raw = await database.ReadRawRecordingAsync(id, cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Id);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, loaded.UpdatedAtUtc.Offset);
        Assert.True(loaded.CreatedAtUtc <= loaded.UpdatedAtUtc);
        Assert.Equal(startedAt.ToUniversalTime(), loaded.StartedAtUtc);
        Assert.Null(loaded.EndedAtUtc);
        Assert.Equal(fileModifiedAt.ToUniversalTime(), loaded.FileModifiedAtUtc);
        Assert.Equal(recording.FilePath, loaded.FilePath);
        Assert.Equal(recording.Status, loaded.Status);
        Assert.Equal(recording.Kind, loaded.Kind);
        Assert.Equal(recording.FileSizeBytes, loaded.FileSizeBytes);
        Assert.Equal(id.ToString("D").ToLowerInvariant(), raw.Id);
        Assert.EndsWith("Z", raw.CreatedAtUtc, StringComparison.Ordinal);
        Assert.EndsWith("Z", raw.UpdatedAtUtc, StringComparison.Ordinal);
        Assert.EndsWith("Z", raw.StartedAtUtc, StringComparison.Ordinal);
        Assert.EndsWith("Z", raw.FileModifiedAtUtc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateChangesExistingRecording()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var repository = new RecordingCatalogRepository(database.ConnectionFactory);
        var id = Guid.Parse("8C88B626-774C-409E-84F0-61290F1545CB");
        var recording = CreateRecording(id, @"D:\Recordings\first.mp4");
        await repository.UpsertAsync(recording, cancellationToken);
        var updated = recording with
        {
            FilePath = @"D:\Recordings\renamed.mp4",
            Status = RecordingCatalogStatus.Recording,
            FileSizeBytes = null,
            FileModifiedAtUtc = null,
        };

        var updatedExisting = await repository.UpdateAsync(updated, cancellationToken);
        var updatedMissing = await repository.UpdateAsync(
            updated with
            {
                Id = Guid.Parse("08871A37-87BD-4F4A-AC95-F30E0B899156"),
            },
            cancellationToken
        );
        var loaded = await repository.GetByIdAsync(id, cancellationToken);

        Assert.True(updatedExisting);
        Assert.False(updatedMissing);
        Assert.NotNull(loaded);
        AssertMatches(updated, loaded);
        Assert.True(loaded.UpdatedAtUtc >= loaded.CreatedAtUtc);
    }

    [Fact]
    public async Task UpsertInsertsAndUpdatesById()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var repository = new RecordingCatalogRepository(database.ConnectionFactory);
        var id = Guid.Parse("58F0B4AC-53A1-48F9-8BE8-A8A69EF9E4CE");
        var recording = CreateRecording(id, @"D:\Recordings\first.mp4");
        var updated = recording with
        {
            FilePath = @"D:\Recordings\second.mp4",
            Status = RecordingCatalogStatus.Recording,
        };

        await repository.UpsertAsync(recording, cancellationToken);
        await repository.UpsertAsync(updated, cancellationToken);

        var recordings = await repository.ListAsync(cancellationToken);

        var loaded = Assert.Single(recordings);
        AssertMatches(updated, loaded);
    }

    [Fact]
    public async Task DeleteRemovesExistingRecording()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var repository = new RecordingCatalogRepository(database.ConnectionFactory);
        var id = Guid.Parse("9D8366BC-3501-42D7-8E45-84C65285E3F3");
        await repository.UpsertAsync(
            CreateRecording(id, @"D:\Recordings\deleted.mp4"),
            cancellationToken
        );

        var deletedExisting = await repository.DeleteAsync(id, cancellationToken);
        var deletedMissing = await repository.DeleteAsync(
            Guid.Parse("6B230E56-29C7-4D2F-B189-3FF91259E51D"),
            cancellationToken
        );

        Assert.True(deletedExisting);
        Assert.False(deletedMissing);
        Assert.Null(await repository.GetByIdAsync(id, cancellationToken));
    }

    private static RecordingCatalogSave CreateRecording(Guid id, string filePath)
    {
        var startedAt = new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.Zero);

        return new RecordingCatalogSave(
            id,
            filePath,
            RecordingCatalogStatus.Available,
            RecordingCatalogKind.Unknown,
            startedAt,
            startedAt.AddMinutes(2),
            2048,
            startedAt.AddMinutes(2)
        );
    }

    private static void AssertMatches(RecordingCatalogSave expected, RecordingCatalogEntry actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.FilePath, actual.FilePath);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.StartedAtUtc?.ToUniversalTime(), actual.StartedAtUtc);
        Assert.Equal(expected.EndedAtUtc?.ToUniversalTime(), actual.EndedAtUtc);
        Assert.Equal(expected.FileSizeBytes, actual.FileSizeBytes);
        Assert.Equal(expected.FileModifiedAtUtc?.ToUniversalTime(), actual.FileModifiedAtUtc);
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
            ConnectionFactory = connectionFactory;
        }

        public SqliteConnectionFactory ConnectionFactory { get; }

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

        public async Task<RawRecording> ReadRawRecordingAsync(
            Guid id,
            CancellationToken cancellationToken
        )
        {
            await using var connection = await ConnectionFactory.OpenConnectionAsync(
                cancellationToken
            );
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, CreatedAtUtc, UpdatedAtUtc, StartedAtUtc, FileModifiedAtUtc
                FROM Recordings
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D").ToLowerInvariant());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));

            return new RawRecording(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)
            );
        }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }

    private sealed record RawRecording(
        string Id,
        string CreatedAtUtc,
        string UpdatedAtUtc,
        string StartedAtUtc,
        string FileModifiedAtUtc
    );

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchRepositoryTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
