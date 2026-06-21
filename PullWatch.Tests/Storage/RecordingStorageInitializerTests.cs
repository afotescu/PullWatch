using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class RecordingStorageInitializerTests
{
    [Fact]
    public async Task RunsInitialMigrationAndCreatesRecordingsSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "pullwatch.db");
        var factory = CreateFactory(databasePath);
        var initializer = new RecordingStorageInitializer(factory, NullLoggerFactory.Instance);

        await initializer.InitializeAsync(cancellationToken);
        await initializer.InitializeAsync(cancellationToken);

        Assert.True(File.Exists(databasePath));
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Recordings';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Recordings_FilePath';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Recordings_CreatedAtUtc';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'RecordingRaidEncounters';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_RecordingRaidEncounters_EncounterId';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                """
                SELECT COUNT(*)
                FROM pragma_foreign_key_list('RecordingRaidEncounters')
                WHERE "table" = 'Recordings'
                    AND "from" = 'RecordingId'
                    AND "to" = 'Id'
                    AND on_delete = 'CASCADE';
                """,
                cancellationToken
            )
        );
    }

    private static SqliteConnectionFactory CreateFactory(string databasePath)
    {
        return new SqliteConnectionFactory(new RecordingDatabasePathProvider(databasePath));
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnectionFactory factory,
        string commandText,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("PullWatchStorageTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
