using Microsoft.Data.Sqlite;

namespace PullWatch;

public sealed class SqliteConnectionFactory
{
    public SqliteConnectionFactory(RecordingDatabasePathProvider pathProvider)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);

        DatabasePath = pathProvider.DatabasePath;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseDirectory();

        var connection = new SqliteConnection(ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal void EnsureDatabaseDirectory()
    {
        var directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
