using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
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
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'RecordingChallengeModes';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_RecordingChallengeModes_ChallengeModeId';",
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                """
                SELECT COUNT(*)
                FROM pragma_foreign_key_list('RecordingChallengeModes')
                WHERE "table" = 'Recordings'
                    AND "from" = 'RecordingId'
                    AND "to" = 'Id'
                    AND on_delete = 'CASCADE';
                """,
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
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('RecordingRaidEncounters')
                WHERE name = 'PullNumber';
                """,
                cancellationToken
            )
        );
        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('Recordings')
                WHERE name = 'IsFavorite'
                    AND "notnull" = 1
                    AND dflt_value = '0';
                """,
                cancellationToken
            )
        );
    }

    [Fact]
    public async Task FavoriteMigrationPreservesExistingRowsWithFalseDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "pullwatch.db");
        var factory = CreateFactory(databasePath);
        using var serviceProvider = CreateMigrationServiceProvider(factory);
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp(202607100001);
        var id = Guid.Parse("6F94B98B-23C6-4FF3-A01B-5B14BC49E7F0");

        await ExecuteAsync(
            factory,
            """
            INSERT INTO Recordings (Id, FilePath, Status, Kind)
            VALUES ($id, 'D:\Recordings\existing.mp4', 'Available', 'Manual');
            """,
            id,
            cancellationToken
        );

        runner.MigrateUp();

        Assert.Equal(
            1L,
            await ScalarLongAsync(
                factory,
                "SELECT COUNT(*) FROM Recordings WHERE Id = $id AND IsFavorite = 0;",
                cancellationToken,
                id
            )
        );
    }

    private static SqliteConnectionFactory CreateFactory(string databasePath)
    {
        return new SqliteConnectionFactory(new RecordingDatabasePathProvider(databasePath));
    }

    private static ServiceProvider CreateMigrationServiceProvider(SqliteConnectionFactory factory)
    {
        return new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner =>
                runner
                    .AddSQLite()
                    .WithGlobalConnectionString(factory.ConnectionString)
                    .ScanIn(typeof(CreateRecordings).Assembly)
                    .For.Migrations()
            )
            .AddLogging()
            .BuildServiceProvider(validateScopes: false);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnectionFactory factory,
        string commandText,
        CancellationToken cancellationToken,
        Guid? id = null
    )
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        if (id is not null)
        {
            command.Parameters.AddWithValue("$id", id.Value.ToString("D").ToLowerInvariant());
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(
        SqliteConnectionFactory factory,
        string commandText,
        Guid id,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$id", id.ToString("D").ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);
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
