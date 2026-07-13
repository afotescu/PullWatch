using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

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
        FileModifiedAtUtc,
        IsFavorite
        """;
    private const string RaidEncounterSelectColumns = """
        RecordingId,
        CreatedAtUtc,
        UpdatedAtUtc,
        EncounterId,
        EncounterName,
        DifficultyId,
        GroupSize,
        InstanceId,
        EncounterStartedAtUtc,
        Outcome,
        EncounterEndedAtUtc,
        DurationMilliseconds,
        PullNumber
        """;
    private const string ChallengeModeSelectColumns = """
        RecordingId,
        CreatedAtUtc,
        UpdatedAtUtc,
        DungeonName,
        MapId,
        ChallengeModeId,
        KeystoneLevel,
        AffixIdsJson,
        ChallengeStartedAtUtc,
        Outcome,
        ChallengeEndedAtUtc,
        TotalTimeMilliseconds,
        OnTimeSeconds,
        MythicRatingAfterRun
        """;

    private readonly SqliteConnectionFactory _connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<bool> UpdateAsync(
        RecordingCatalogSave recording,
        CancellationToken cancellationToken
    )
    {
        return await UpdateAsync(recording, null, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        RecordingCatalogSave recording,
        RaidEncounterCompletionSave? raidEncounterCompletion,
        CancellationToken cancellationToken
    )
    {
        return await UpdateAsync(recording, null, raidEncounterCompletion, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        RecordingCatalogSave recording,
        ChallengeModeCompletionSave? challengeModeCompletion,
        RaidEncounterCompletionSave? raidEncounterCompletion,
        CancellationToken cancellationToken
    )
    {
        Validate(recording);
        Validate(challengeModeCompletion);
        Validate(raidEncounterCompletion);

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        using var transaction = connection.BeginTransaction();

        var affectedRows = await ExecuteRecordingUpdateAsync(
            connection,
            transaction,
            recording,
            cancellationToken
        );

        if (affectedRows > 0 && challengeModeCompletion is not null)
        {
            await ExecuteChallengeModeCompletionUpdateAsync(
                connection,
                transaction,
                challengeModeCompletion,
                cancellationToken
            );
        }

        if (affectedRows > 0 && raidEncounterCompletion is not null)
        {
            await ExecuteRaidEncounterCompletionUpdateAsync(
                connection,
                transaction,
                raidEncounterCompletion,
                cancellationToken
            );
        }

        transaction.Commit();

        return affectedRows > 0;
    }

    public async Task UpsertAsync(
        RecordingCatalogSave recording,
        CancellationToken cancellationToken
    )
    {
        await UpsertAsync(recording, null, cancellationToken);
    }

    public async Task UpsertAsync(
        RecordingCatalogSave recording,
        RaidEncounterSave? raidEncounter,
        CancellationToken cancellationToken
    )
    {
        await UpsertAsync(recording, null, raidEncounter, cancellationToken);
    }

    public async Task UpsertAsync(
        RecordingCatalogSave recording,
        ChallengeModeSave? challengeMode,
        RaidEncounterSave? raidEncounter,
        CancellationToken cancellationToken
    )
    {
        Validate(recording);
        Validate(challengeMode);
        Validate(raidEncounter);

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        using var transaction = connection.BeginTransaction();

        await ExecuteRecordingUpsertAsync(connection, transaction, recording, cancellationToken);

        if (challengeMode is not null)
        {
            await ExecuteChallengeModeUpsertAsync(
                connection,
                transaction,
                challengeMode,
                cancellationToken
            );
        }

        if (raidEncounter is not null)
        {
            await ExecuteRaidEncounterUpsertAsync(
                connection,
                transaction,
                raidEncounter,
                cancellationToken
            );
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<RaidEncounterEntry>> ListRaidEncountersByRecordingIdsAsync(
        IReadOnlyCollection<Guid> recordingIds,
        CancellationToken cancellationToken
    )
    {
        if (recordingIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var rows = await connection.QueryAsync<RaidEncounterRow>(
            new CommandDefinition(
                $"""
                SELECT {RaidEncounterSelectColumns}
                FROM RecordingRaidEncounters
                WHERE RecordingId IN @RecordingIds;
                """,
                new { RecordingIds = recordingIds.Select(FormatId).ToArray() },
                cancellationToken: cancellationToken
            )
        );

        return rows.Select(FromRow).ToList();
    }

    public async Task<IReadOnlyList<ChallengeModeEntry>> ListChallengeModesByRecordingIdsAsync(
        IReadOnlyCollection<Guid> recordingIds,
        CancellationToken cancellationToken
    )
    {
        if (recordingIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var rows = await connection.QueryAsync<ChallengeModeRow>(
            new CommandDefinition(
                $"""
                SELECT {ChallengeModeSelectColumns}
                FROM RecordingChallengeModes
                WHERE RecordingId IN @RecordingIds;
                """,
                new { RecordingIds = recordingIds.Select(FormatId).ToArray() },
                cancellationToken: cancellationToken
            )
        );

        return rows.Select(FromRow).ToList();
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

    public async Task<bool> SetFavoriteAsync(
        Guid id,
        bool isFavorite,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(
            cancellationToken
        );
        var affectedRows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE Recordings
                SET IsFavorite = @IsFavorite
                WHERE Id = @Id;
                """,
                new { Id = FormatId(id), IsFavorite = isFavorite },
                cancellationToken: cancellationToken
            )
        );

        return affectedRows > 0;
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

    private static async Task<int> ExecuteRecordingUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordingCatalogSave recording,
        CancellationToken cancellationToken
    )
    {
        return await connection.ExecuteAsync(
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
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task ExecuteRecordingUpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordingCatalogSave recording,
        CancellationToken cancellationToken
    )
    {
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
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task ExecuteChallengeModeUpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChallengeModeSave challengeMode,
        CancellationToken cancellationToken
    )
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO RecordingChallengeModes (
                    RecordingId,
                    DungeonName,
                    MapId,
                    ChallengeModeId,
                    KeystoneLevel,
                    AffixIdsJson,
                    ChallengeStartedAtUtc,
                    Outcome,
                    ChallengeEndedAtUtc,
                    TotalTimeMilliseconds,
                    OnTimeSeconds,
                    MythicRatingAfterRun
                )
                VALUES (
                    @RecordingId,
                    @DungeonName,
                    @MapId,
                    @ChallengeModeId,
                    @KeystoneLevel,
                    @AffixIdsJson,
                    @ChallengeStartedAtUtc,
                    @Outcome,
                    @ChallengeEndedAtUtc,
                    @TotalTimeMilliseconds,
                    @OnTimeSeconds,
                    @MythicRatingAfterRun
                )
                ON CONFLICT(RecordingId) DO UPDATE SET
                    DungeonName = excluded.DungeonName,
                    MapId = excluded.MapId,
                    ChallengeModeId = excluded.ChallengeModeId,
                    KeystoneLevel = excluded.KeystoneLevel,
                    AffixIdsJson = excluded.AffixIdsJson,
                    ChallengeStartedAtUtc = excluded.ChallengeStartedAtUtc,
                    Outcome = excluded.Outcome,
                    ChallengeEndedAtUtc = excluded.ChallengeEndedAtUtc,
                    TotalTimeMilliseconds = excluded.TotalTimeMilliseconds,
                    OnTimeSeconds = excluded.OnTimeSeconds,
                    MythicRatingAfterRun = excluded.MythicRatingAfterRun;
                """,
                ToParameters(challengeMode),
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task ExecuteRaidEncounterUpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RaidEncounterSave raidEncounter,
        CancellationToken cancellationToken
    )
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO RecordingRaidEncounters (
                    RecordingId,
                    EncounterId,
                    EncounterName,
                    DifficultyId,
                    GroupSize,
                    InstanceId,
                    EncounterStartedAtUtc,
                    Outcome,
                    EncounterEndedAtUtc,
                    DurationMilliseconds,
                    PullNumber
                )
                VALUES (
                    @RecordingId,
                    @EncounterId,
                    @EncounterName,
                    @DifficultyId,
                    @GroupSize,
                    @InstanceId,
                    @EncounterStartedAtUtc,
                    @Outcome,
                    @EncounterEndedAtUtc,
                    @DurationMilliseconds,
                    @PullNumber
                )
                ON CONFLICT(RecordingId) DO UPDATE SET
                    EncounterId = excluded.EncounterId,
                    EncounterName = excluded.EncounterName,
                    DifficultyId = excluded.DifficultyId,
                    GroupSize = excluded.GroupSize,
                    InstanceId = excluded.InstanceId,
                    EncounterStartedAtUtc = excluded.EncounterStartedAtUtc,
                    Outcome = excluded.Outcome,
                    EncounterEndedAtUtc = excluded.EncounterEndedAtUtc,
                    DurationMilliseconds = excluded.DurationMilliseconds,
                    PullNumber = excluded.PullNumber;
                """,
                ToParameters(raidEncounter),
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task ExecuteChallengeModeCompletionUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChallengeModeCompletionSave challengeModeCompletion,
        CancellationToken cancellationToken
    )
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE RecordingChallengeModes
                SET
                    Outcome = @Outcome,
                    ChallengeEndedAtUtc = @ChallengeEndedAtUtc,
                    TotalTimeMilliseconds = @TotalTimeMilliseconds,
                    OnTimeSeconds = @OnTimeSeconds,
                    MythicRatingAfterRun = @MythicRatingAfterRun
                WHERE RecordingId = @RecordingId;
                """,
                ToParameters(challengeModeCompletion),
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task ExecuteRaidEncounterCompletionUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RaidEncounterCompletionSave raidEncounterCompletion,
        CancellationToken cancellationToken
    )
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE RecordingRaidEncounters
                SET
                    Outcome = @Outcome,
                    EncounterEndedAtUtc = @EncounterEndedAtUtc,
                    DurationMilliseconds = @DurationMilliseconds
                WHERE RecordingId = @RecordingId;
                """,
                ToParameters(raidEncounterCompletion),
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static void Validate(RecordingCatalogSave recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentException.ThrowIfNullOrWhiteSpace(recording.FilePath);
    }

    private static void Validate(ChallengeModeSave? challengeMode)
    {
        if (challengeMode is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(challengeMode.DungeonName);
        ArgumentNullException.ThrowIfNull(challengeMode.AffixIds);
    }

    private static void Validate(RaidEncounterSave? raidEncounter)
    {
        if (raidEncounter is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(raidEncounter.EncounterName);
    }

    private static void Validate(ChallengeModeCompletionSave? challengeModeCompletion)
    {
        if (challengeModeCompletion is null)
        {
            return;
        }

        if (challengeModeCompletion.Outcome == ChallengeModeOutcome.Unknown)
        {
            throw new ArgumentException(
                "Challenge mode completion must have a timed or depleted outcome.",
                nameof(challengeModeCompletion)
            );
        }
    }

    private static void Validate(RaidEncounterCompletionSave? raidEncounterCompletion)
    {
        if (raidEncounterCompletion is null)
        {
            return;
        }

        if (raidEncounterCompletion.Outcome == RaidEncounterOutcome.Unknown)
        {
            throw new ArgumentException(
                "Raid encounter completion must have a kill or wipe outcome.",
                nameof(raidEncounterCompletion)
            );
        }
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
            StorageTimestampFormatter.ParseNullableUtc(row.FileModifiedAtUtc),
            row.IsFavorite != 0
        );
    }

    private static RaidEncounterEntry FromRow(RaidEncounterRow row)
    {
        return new RaidEncounterEntry(
            Guid.Parse(row.RecordingId),
            StorageTimestampFormatter.ParseUtc(row.CreatedAtUtc),
            StorageTimestampFormatter.ParseUtc(row.UpdatedAtUtc),
            ToInt32(row.EncounterId),
            row.EncounterName,
            ToInt32(row.DifficultyId),
            ToNullableInt32(row.GroupSize),
            ToNullableInt32(row.InstanceId),
            StorageTimestampFormatter.ParseUtc(row.EncounterStartedAtUtc),
            ParseEnum<RaidEncounterOutcome>(row.Outcome),
            StorageTimestampFormatter.ParseNullableUtc(row.EncounterEndedAtUtc),
            ToNullableInt32(row.DurationMilliseconds),
            ToNullableInt32(row.PullNumber)
        );
    }

    private static ChallengeModeEntry FromRow(ChallengeModeRow row)
    {
        return new ChallengeModeEntry(
            Guid.Parse(row.RecordingId),
            StorageTimestampFormatter.ParseUtc(row.CreatedAtUtc),
            StorageTimestampFormatter.ParseUtc(row.UpdatedAtUtc),
            row.DungeonName,
            ToInt32(row.MapId),
            ToInt32(row.ChallengeModeId),
            ToInt32(row.KeystoneLevel),
            ParseAffixIds(row.AffixIdsJson),
            StorageTimestampFormatter.ParseUtc(row.ChallengeStartedAtUtc),
            ParseEnum<ChallengeModeOutcome>(row.Outcome),
            StorageTimestampFormatter.ParseNullableUtc(row.ChallengeEndedAtUtc),
            ToNullableInt32(row.TotalTimeMilliseconds),
            row.OnTimeSeconds,
            ToNullableInt32(row.MythicRatingAfterRun)
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

    private static int ToInt32(long value)
    {
        return checked((int)value);
    }

    private static int? ToNullableInt32(long? value)
    {
        return value is null ? null : ToInt32(value.Value);
    }

    private static string FormatAffixIds(IReadOnlyList<int> affixIds)
    {
        return JsonSerializer.Serialize(affixIds);
    }

    private static IReadOnlyList<int> ParseAffixIds(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<int[]>(value)
                ?? throw new InvalidOperationException("Affix ID list cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Database value is not a valid affix ID list.",
                exception
            );
        }
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

    private static ChallengeModeParameters ToParameters(ChallengeModeSave challengeMode)
    {
        return new ChallengeModeParameters(
            FormatId(challengeMode.RecordingId),
            challengeMode.DungeonName,
            challengeMode.MapId,
            challengeMode.ChallengeModeId,
            challengeMode.KeystoneLevel,
            FormatAffixIds(challengeMode.AffixIds),
            StorageTimestampFormatter.FormatUtc(challengeMode.ChallengeStartedAtUtc),
            challengeMode.Outcome.ToString(),
            StorageTimestampFormatter.FormatNullableUtc(challengeMode.ChallengeEndedAtUtc),
            challengeMode.TotalTimeMilliseconds,
            challengeMode.OnTimeSeconds,
            challengeMode.MythicRatingAfterRun
        );
    }

    private static RaidEncounterParameters ToParameters(RaidEncounterSave raidEncounter)
    {
        return new RaidEncounterParameters(
            FormatId(raidEncounter.RecordingId),
            raidEncounter.EncounterId,
            raidEncounter.EncounterName,
            raidEncounter.DifficultyId,
            raidEncounter.GroupSize,
            raidEncounter.InstanceId,
            StorageTimestampFormatter.FormatUtc(raidEncounter.EncounterStartedAtUtc),
            raidEncounter.Outcome.ToString(),
            StorageTimestampFormatter.FormatNullableUtc(raidEncounter.EncounterEndedAtUtc),
            raidEncounter.DurationMilliseconds,
            raidEncounter.PullNumber
        );
    }

    private static ChallengeModeCompletionParameters ToParameters(
        ChallengeModeCompletionSave challengeModeCompletion
    )
    {
        return new ChallengeModeCompletionParameters(
            FormatId(challengeModeCompletion.RecordingId),
            challengeModeCompletion.Outcome.ToString(),
            StorageTimestampFormatter.FormatUtc(challengeModeCompletion.ChallengeEndedAtUtc),
            challengeModeCompletion.TotalTimeMilliseconds,
            challengeModeCompletion.OnTimeSeconds,
            challengeModeCompletion.MythicRatingAfterRun
        );
    }

    private static RaidEncounterCompletionParameters ToParameters(
        RaidEncounterCompletionSave raidEncounterCompletion
    )
    {
        return new RaidEncounterCompletionParameters(
            FormatId(raidEncounterCompletion.RecordingId),
            raidEncounterCompletion.Outcome.ToString(),
            StorageTimestampFormatter.FormatUtc(raidEncounterCompletion.EncounterEndedAtUtc),
            raidEncounterCompletion.DurationMilliseconds
        );
    }

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
        string? FileModifiedAtUtc,
        long IsFavorite
    );

    private sealed record ChallengeModeParameters(
        string RecordingId,
        string DungeonName,
        int MapId,
        int ChallengeModeId,
        int KeystoneLevel,
        string AffixIdsJson,
        string ChallengeStartedAtUtc,
        string Outcome,
        string? ChallengeEndedAtUtc,
        int? TotalTimeMilliseconds,
        double? OnTimeSeconds,
        int? MythicRatingAfterRun
    );

    private sealed record RaidEncounterParameters(
        string RecordingId,
        int EncounterId,
        string EncounterName,
        int DifficultyId,
        int? GroupSize,
        int? InstanceId,
        string EncounterStartedAtUtc,
        string Outcome,
        string? EncounterEndedAtUtc,
        int? DurationMilliseconds,
        int? PullNumber
    );

    private sealed record ChallengeModeCompletionParameters(
        string RecordingId,
        string Outcome,
        string ChallengeEndedAtUtc,
        int? TotalTimeMilliseconds,
        double? OnTimeSeconds,
        int? MythicRatingAfterRun
    );

    private sealed record RaidEncounterCompletionParameters(
        string RecordingId,
        string Outcome,
        string EncounterEndedAtUtc,
        int? DurationMilliseconds
    );

    private sealed record ChallengeModeRow(
        string RecordingId,
        string CreatedAtUtc,
        string UpdatedAtUtc,
        string DungeonName,
        long MapId,
        long ChallengeModeId,
        long KeystoneLevel,
        string AffixIdsJson,
        string ChallengeStartedAtUtc,
        string Outcome,
        string? ChallengeEndedAtUtc,
        long? TotalTimeMilliseconds,
        double? OnTimeSeconds,
        long? MythicRatingAfterRun
    );

    private sealed record RaidEncounterRow(
        string RecordingId,
        string CreatedAtUtc,
        string UpdatedAtUtc,
        long EncounterId,
        string EncounterName,
        long DifficultyId,
        long? GroupSize,
        long? InstanceId,
        string EncounterStartedAtUtc,
        string Outcome,
        string? EncounterEndedAtUtc,
        long? DurationMilliseconds,
        long? PullNumber
    );
}
