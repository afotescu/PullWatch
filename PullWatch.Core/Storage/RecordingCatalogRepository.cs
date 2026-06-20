using Dapper;

namespace PullWatch;

public sealed class RecordingCatalogRepository(SqliteConnectionFactory connectionFactory)
{
    private const string SelectColumns = """
        Id,
        CreatedAtUtc,
        UpdatedAtUtc,
        FilePath,
        Status,
        Kind,
        StartedAtUtc,
        EndedAtUtc,
        FileSizeBytes,
        FileModifiedAtUtc
        """;

    private readonly SqliteConnectionFactory _connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task AddAsync(RecordingCatalogSave recording, CancellationToken cancellationToken)
    {
        Validate(recording);

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO Recordings (
                    Id,
                    FilePath,
                    Status,
                    Kind,
                    StartedAtUtc,
                    EndedAtUtc,
                    FileSizeBytes,
                    FileModifiedAtUtc
                )
                VALUES (
                    @Id,
                    @FilePath,
                    @Status,
                    @Kind,
                    @StartedAtUtc,
                    @EndedAtUtc,
                    @FileSizeBytes,
                    @FileModifiedAtUtc
                );
                """,
                ToParameters(recording),
                cancellationToken: cancellationToken
            )
        );
    }

    public async Task<bool> UpdateAsync(
        RecordingCatalogSave recording,
        CancellationToken cancellationToken
    )
    {
        Validate(recording);

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var affectedRows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE Recordings
                SET
                    FilePath = @FilePath,
                    Status = @Status,
                    Kind = @Kind,
                    StartedAtUtc = @StartedAtUtc,
                    EndedAtUtc = @EndedAtUtc,
                    FileSizeBytes = @FileSizeBytes,
                    FileModifiedAtUtc = @FileModifiedAtUtc
                WHERE Id = @Id;
                """,
                ToParameters(recording),
                cancellationToken: cancellationToken
            )
        );

        return affectedRows > 0;
    }

    public async Task UpsertAsync(
        RecordingCatalogSave recording,
        CancellationToken cancellationToken
    )
    {
        Validate(recording);

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO Recordings (
                    Id,
                    FilePath,
                    Status,
                    Kind,
                    StartedAtUtc,
                    EndedAtUtc,
                    FileSizeBytes,
                    FileModifiedAtUtc
                )
                VALUES (
                    @Id,
                    @FilePath,
                    @Status,
                    @Kind,
                    @StartedAtUtc,
                    @EndedAtUtc,
                    @FileSizeBytes,
                    @FileModifiedAtUtc
                )
                ON CONFLICT(Id) DO UPDATE SET
                    FilePath = excluded.FilePath,
                    Status = excluded.Status,
                    Kind = excluded.Kind,
                    StartedAtUtc = excluded.StartedAtUtc,
                    EndedAtUtc = excluded.EndedAtUtc,
                    FileSizeBytes = excluded.FileSizeBytes,
                    FileModifiedAtUtc = excluded.FileModifiedAtUtc;
                """,
                ToParameters(recording),
                cancellationToken: cancellationToken
            )
        );
    }

    public async Task<RecordingCatalogEntry?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var row = await connection.QuerySingleOrDefaultAsync<RecordingCatalogRow>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                FROM Recordings
                WHERE Id = @Id;
                """,
                new { Id = FormatId(id) },
                cancellationToken: cancellationToken
            )
        );

        return row is null ? null : FromRow(row);
    }

    public async Task<RecordingCatalogEntry?> GetByFilePathAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var row = await connection.QuerySingleOrDefaultAsync<RecordingCatalogRow>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                FROM Recordings
                WHERE FilePath = @FilePath;
                """,
                new { FilePath = filePath },
                cancellationToken: cancellationToken
            )
        );

        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<RecordingCatalogEntry>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var rows = await connection.QueryAsync<RecordingCatalogRow>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                FROM Recordings
                ORDER BY CreatedAtUtc DESC, Id ASC;
                """,
                cancellationToken: cancellationToken
            )
        );

        return rows.Select(FromRow).ToList();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var affectedRows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM Recordings
                WHERE Id = @Id;
                """,
                new { Id = FormatId(id) },
                cancellationToken: cancellationToken
            )
        );

        return affectedRows > 0;
    }

    private static void Validate(RecordingCatalogSave recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentException.ThrowIfNullOrWhiteSpace(recording.FilePath);
    }

    private static RecordingCatalogParameters ToParameters(RecordingCatalogSave recording)
    {
        return new RecordingCatalogParameters(
            FormatId(recording.Id),
            recording.FilePath,
            recording.Status.ToString(),
            recording.Kind.ToString(),
            StorageTimestampFormatter.FormatNullableUtc(recording.StartedAtUtc),
            StorageTimestampFormatter.FormatNullableUtc(recording.EndedAtUtc),
            recording.FileSizeBytes,
            StorageTimestampFormatter.FormatNullableUtc(recording.FileModifiedAtUtc)
        );
    }

    private static RecordingCatalogEntry FromRow(RecordingCatalogRow row)
    {
        return new RecordingCatalogEntry(
            Guid.Parse(row.Id),
            StorageTimestampFormatter.ParseUtc(row.CreatedAtUtc),
            StorageTimestampFormatter.ParseUtc(row.UpdatedAtUtc),
            row.FilePath,
            ParseEnum<RecordingCatalogStatus>(row.Status),
            ParseEnum<RecordingCatalogKind>(row.Kind),
            StorageTimestampFormatter.ParseNullableUtc(row.StartedAtUtc),
            StorageTimestampFormatter.ParseNullableUtc(row.EndedAtUtc),
            row.FileSizeBytes,
            StorageTimestampFormatter.ParseNullableUtc(row.FileModifiedAtUtc)
        );
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Database value '{value}' is not a valid {typeof(TEnum).Name}."
            );
    }

    private static string FormatId(Guid id)
    {
        return id.ToString("D").ToLowerInvariant();
    }

    private sealed record RecordingCatalogParameters(
        string Id,
        string FilePath,
        string Status,
        string Kind,
        string? StartedAtUtc,
        string? EndedAtUtc,
        long? FileSizeBytes,
        string? FileModifiedAtUtc
    );

    private sealed record RecordingCatalogRow(
        string Id,
        string CreatedAtUtc,
        string UpdatedAtUtc,
        string FilePath,
        string Status,
        string Kind,
        string? StartedAtUtc,
        string? EndedAtUtc,
        long? FileSizeBytes,
        string? FileModifiedAtUtc
    );
}
