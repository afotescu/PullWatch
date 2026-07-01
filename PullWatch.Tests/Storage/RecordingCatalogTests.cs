using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class RecordingCatalogTests
{
    [Fact]
    public async Task BeginAndCompleteRecordingStoresLifecycleMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(
            new RecordingCatalogRepository(database.ConnectionFactory)
        );
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var outputPath = Path.Combine(directory.FullName, "challenge.mp4");
        var startedAt = new DateTimeOffset(2026, 6, 20, 12, 30, 0, TimeSpan.FromHours(3));
        var modifiedAtUtc = new DateTime(2026, 6, 20, 10, 35, 0, DateTimeKind.Utc);
        var context = new ChallengeRecordingContext(
            startedAt,
            "Magisters' Terrace",
            2811,
            558,
            22,
            [9, 10, 147]
        );

        var id = await catalog.BeginRecordingAsync(context, outputPath, cancellationToken);
        var started = await database.Repository.GetByIdAsync(id, cancellationToken);

        File.WriteAllText(outputPath, "recording");
        File.SetLastWriteTimeUtc(outputPath, modifiedAtUtc);
        var endedAt = new DateTimeOffset(2026, 6, 20, 10, 35, 5, TimeSpan.Zero);
        await catalog.CompleteRecordingAsync(id, endedAt, cancellationToken);

        var completed = await database.Repository.GetByIdAsync(id, cancellationToken);

        Assert.NotNull(started);
        Assert.Equal(RecordingCatalogStatus.Recording, started.Status);
        Assert.Equal(RecordingCatalogKind.ChallengeMode, started.Kind);
        Assert.Equal(startedAt.ToUniversalTime(), started.StartedAtUtc);
        Assert.Null(started.EndedAtUtc);
        Assert.Null(started.FileSizeBytes);
        Assert.Null(started.FileModifiedAtUtc);

        Assert.NotNull(completed);
        Assert.Equal(RecordingCatalogStatus.Available, completed.Status);
        Assert.Equal(startedAt.ToUniversalTime(), completed.StartedAtUtc);
        Assert.Equal(endedAt, completed.EndedAtUtc);
        Assert.Equal(9, completed.FileSizeBytes);
        Assert.Equal(new DateTimeOffset(modifiedAtUtc), completed.FileModifiedAtUtc);
    }

    [Fact]
    public async Task ChallengeModeRecordingStoresMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var outputPath = Path.Combine(directory.FullName, "magisters-terrace.mp4");
        var startedAt = new DateTimeOffset(2026, 6, 14, 23, 37, 55, TimeSpan.FromHours(3));
        var context = new ChallengeRecordingContext(
            startedAt,
            "Magisters' Terrace",
            2811,
            558,
            22,
            [9, 10, 147]
        );

        var id = await catalog.BeginRecordingAsync(context, outputPath, cancellationToken);
        var startedChallengeMode = await FindChallengeModeByRecordingIdAsync(
            database.Repository,
            id,
            cancellationToken
        );

        File.WriteAllText(outputPath, "recording");
        var challengeEndedAt = new DateTimeOffset(2026, 6, 15, 0, 8, 45, TimeSpan.FromHours(3));
        await catalog.CompleteRecordingAsync(
            id,
            challengeEndedAt.AddSeconds(1),
            new ChallengeRecordingEnd(
                challengeEndedAt,
                2811,
                ChallengeModeOutcome.Timed,
                22,
                1850000,
                32.5,
                1800
            ),
            cancellationToken
        );

        var completedChallengeMode = await FindChallengeModeByRecordingIdAsync(
            database.Repository,
            id,
            cancellationToken
        );

        Assert.NotNull(startedChallengeMode);
        Assert.Equal("Magisters' Terrace", startedChallengeMode.DungeonName);
        Assert.Equal(2811, startedChallengeMode.MapId);
        Assert.Equal(558, startedChallengeMode.ChallengeModeId);
        Assert.Equal(22, startedChallengeMode.KeystoneLevel);
        Assert.Equal([9, 10, 147], startedChallengeMode.AffixIds);
        Assert.Equal(startedAt.ToUniversalTime(), startedChallengeMode.ChallengeStartedAtUtc);
        Assert.Equal(ChallengeModeOutcome.Unknown, startedChallengeMode.Outcome);
        Assert.Null(startedChallengeMode.ChallengeEndedAtUtc);
        Assert.Null(startedChallengeMode.TotalTimeMilliseconds);

        Assert.NotNull(completedChallengeMode);
        Assert.Equal(ChallengeModeOutcome.Timed, completedChallengeMode.Outcome);
        Assert.Equal(
            challengeEndedAt.ToUniversalTime(),
            completedChallengeMode.ChallengeEndedAtUtc
        );
        Assert.Equal(1850000, completedChallengeMode.TotalTimeMilliseconds);
        Assert.Equal(32.5, completedChallengeMode.OnTimeSeconds);
        Assert.Equal(1800, completedChallengeMode.MythicRatingAfterRun);

        var recordings = await catalog.ListAvailableFilesAsync(
            directory.FullName,
            cancellationToken
        );
        var recording = Assert.Single(recordings);
        Assert.NotNull(recording.ChallengeMode);
        Assert.Equal("Magisters' Terrace", recording.ChallengeMode.DungeonName);
        Assert.Equal(ChallengeModeOutcome.Timed, recording.ChallengeMode.Outcome);
        Assert.Equal(1850000, recording.ChallengeMode.TotalTimeMilliseconds);

        await catalog.DeleteAvailableRecordingAsync(id, directory.FullName, cancellationToken);

        Assert.Null(
            await FindChallengeModeByRecordingIdAsync(database.Repository, id, cancellationToken)
        );
    }

    [Fact]
    public async Task EncounterRecordingStoresRaidEncounterMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var outputPath = Path.Combine(directory.FullName, "rotmire.mp4");
        var startedAt = new DateTimeOffset(2026, 6, 17, 23, 28, 32, TimeSpan.FromHours(3));
        var context = new EncounterRecordingContext(
            startedAt,
            3159,
            "Rotmire",
            WowDifficultyIds.FlexibleMythicRaid,
            20,
            1592,
            7
        );

        var id = await catalog.BeginRecordingAsync(context, outputPath, cancellationToken);
        var startedEncounter = await FindRaidEncounterByRecordingIdAsync(
            database.Repository,
            id,
            cancellationToken
        );

        File.WriteAllText(outputPath, "recording");
        var encounterEndedAt = new DateTimeOffset(2026, 6, 17, 23, 36, 19, TimeSpan.FromHours(3));
        await catalog.CompleteRecordingAsync(
            id,
            encounterEndedAt.AddSeconds(1),
            new EncounterRecordingEnd(
                encounterEndedAt,
                3159,
                "Rotmire",
                WowDifficultyIds.FlexibleMythicRaid,
                20,
                RaidEncounterOutcome.Kill,
                466563
            ),
            cancellationToken
        );

        var completedEncounter = await FindRaidEncounterByRecordingIdAsync(
            database.Repository,
            id,
            cancellationToken
        );

        Assert.NotNull(startedEncounter);
        Assert.Equal(3159, startedEncounter.EncounterId);
        Assert.Equal("Rotmire", startedEncounter.EncounterName);
        Assert.Equal(WowDifficultyIds.FlexibleMythicRaid, startedEncounter.DifficultyId);
        Assert.Equal(20, startedEncounter.GroupSize);
        Assert.Equal(1592, startedEncounter.InstanceId);
        Assert.Equal(7, startedEncounter.PullNumber);
        Assert.Equal(startedAt.ToUniversalTime(), startedEncounter.EncounterStartedAtUtc);
        Assert.Equal(RaidEncounterOutcome.Unknown, startedEncounter.Outcome);
        Assert.Null(startedEncounter.EncounterEndedAtUtc);
        Assert.Null(startedEncounter.DurationMilliseconds);

        Assert.NotNull(completedEncounter);
        Assert.Equal(RaidEncounterOutcome.Kill, completedEncounter.Outcome);
        Assert.Equal(encounterEndedAt.ToUniversalTime(), completedEncounter.EncounterEndedAtUtc);
        Assert.Equal(466563, completedEncounter.DurationMilliseconds);

        var recordings = await catalog.ListAvailableFilesAsync(
            directory.FullName,
            cancellationToken
        );
        var recording = Assert.Single(recordings);
        Assert.NotNull(recording.RaidEncounter);
        Assert.Equal("Rotmire", recording.RaidEncounter.EncounterName);
        Assert.Equal(WowDifficultyIds.FlexibleMythicRaid, recording.RaidEncounter.DifficultyId);
        Assert.Equal(7, recording.RaidEncounter.PullNumber);
        Assert.Equal(RaidEncounterOutcome.Kill, recording.RaidEncounter.Outcome);
        Assert.Equal(466563, recording.RaidEncounter.DurationMilliseconds);

        await catalog.DeleteAvailableRecordingAsync(id, directory.FullName, cancellationToken);

        Assert.Null(
            await FindRaidEncounterByRecordingIdAsync(database.Repository, id, cancellationToken)
        );
    }

    [Fact]
    public async Task ListAvailableFilesVerifiesRowsWithoutImportingLooseFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var subdirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Nested"));
        var outsideDirectory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Outside")
        );
        var catalogedPath = WriteFile(directory.FullName, "cataloged.mp4", "cataloged");
        var loosePath = WriteFile(directory.FullName, "loose.mp4", "loose");
        var subdirectoryPath = WriteFile(subdirectory.FullName, "nested.mp4", "nested");
        var outsidePath = WriteFile(outsideDirectory.FullName, "outside.mp4", "outside");
        var missingId = Guid.Parse("A5472C19-6BA0-4472-B2C2-0B28051407B6");
        var missingOutsideId = Guid.Parse("859839D9-7035-49C8-9FBD-AE6250D70A78");
        var recordingId = Guid.Parse("2943E43C-3F48-4D90-BB8F-3B2E70DBCD34");

        await database.Repository.UpsertAsync(
            CreateSave(Guid.Parse("82031F2F-16C0-4338-B622-484B03AB6980"), catalogedPath),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(missingId, Path.Combine(directory.FullName, "missing.mp4")),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(Guid.Parse("6E4A41A4-4A20-4167-8F9E-6A31676F72D6"), subdirectoryPath),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(Guid.Parse("B58C239C-A5F6-418C-8840-62362C334F79"), outsidePath),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(missingOutsideId, Path.Combine(outsideDirectory.FullName, "missing.mp4")),
            cancellationToken
        );
        await database.Repository.UpsertAsync(
            CreateSave(recordingId, Path.Combine(directory.FullName, "active.mp4")) with
            {
                Status = RecordingCatalogStatus.Recording,
            },
            cancellationToken
        );

        var recordings = await catalog.ListAvailableFilesAsync(
            directory.FullName,
            cancellationToken
        );

        var recording = Assert.Single(recordings);
        Assert.Equal(catalogedPath, recording.FilePath);
        Assert.Equal("cataloged.mp4", Path.GetFileName(recording.FilePath));
        Assert.Equal(9, recording.SizeBytes);
        var catalogEntries = await database.Repository.ListAsync(cancellationToken);
        Assert.DoesNotContain(catalogEntries, entry => entry.FilePath == loosePath);
        Assert.Null(await database.Repository.GetByIdAsync(missingId, cancellationToken));
        Assert.Null(await database.Repository.GetByIdAsync(missingOutsideId, cancellationToken));
        Assert.Equal(
            RecordingCatalogStatus.Recording,
            (await database.Repository.GetByIdAsync(recordingId, cancellationToken))!.Status
        );
    }

    [Fact]
    public async Task DeleteAvailableRecordingRemovesFileAndCatalogRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var id = Guid.Parse("5884C48E-B6CD-4F9A-B0B5-E0572AB54A6B");
        var path = WriteFile(directory.FullName, "deleted.mp4", "deleted");
        await database.Repository.UpsertAsync(CreateSave(id, path), cancellationToken);

        await catalog.DeleteAvailableRecordingAsync(id, directory.FullName, cancellationToken);

        Assert.False(File.Exists(path));
        Assert.Null(await database.Repository.GetByIdAsync(id, cancellationToken));
    }

    [Fact]
    public async Task DeleteAvailableRecordingRemovesCatalogRowWhenFileIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var id = Guid.Parse("5F0CFB5C-A5A9-4652-BA4B-21956E7D8BE5");
        await database.Repository.UpsertAsync(
            CreateSave(id, Path.Combine(directory.FullName, "missing.mp4")),
            cancellationToken
        );

        await catalog.DeleteAvailableRecordingAsync(id, directory.FullName, cancellationToken);

        Assert.Null(await database.Repository.GetByIdAsync(id, cancellationToken));
    }

    [Fact]
    public async Task DeleteAvailableRecordingRejectsPathOutsideRecordingsDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var outsideDirectory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Outside")
        );
        var id = Guid.Parse("6F8E9651-6267-4D7A-A8E0-D3BB85DFE0FB");
        var path = WriteFile(outsideDirectory.FullName, "outside.mp4", "outside");
        await database.Repository.UpsertAsync(CreateSave(id, path), cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.DeleteAvailableRecordingAsync(id, directory.FullName, cancellationToken)
        );

        Assert.Equal(
            "Only recordings in the active recordings directory can be deleted.",
            exception.Message
        );
        Assert.True(File.Exists(path));
        Assert.NotNull(await database.Repository.GetByIdAsync(id, cancellationToken));
    }

    [Fact]
    public async Task DeleteAvailableRecordingRejectsActiveRecording()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var database = await TemporaryRecordingDatabase.CreateAsync(cancellationToken);
        var catalog = new RecordingCatalog(database.Repository);
        var directory = Directory.CreateDirectory(
            Path.Combine(database.DirectoryPath, "Recordings")
        );
        var id = Guid.Parse("59C6BFC0-372A-4776-A1A9-1F0F75EE5A5C");
        var path = WriteFile(directory.FullName, "active.mp4", "active");
        await database.Repository.UpsertAsync(
            CreateSave(id, path) with
            {
                Status = RecordingCatalogStatus.Recording,
            },
            cancellationToken
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.DeleteAvailableRecordingAsync(id, directory.FullName, cancellationToken)
        );

        Assert.True(File.Exists(path));
        Assert.NotNull(await database.Repository.GetByIdAsync(id, cancellationToken));
    }

    private static async Task<ChallengeModeEntry?> FindChallengeModeByRecordingIdAsync(
        RecordingCatalogRepository repository,
        Guid recordingId,
        CancellationToken cancellationToken
    )
    {
        var entries = await repository.ListChallengeModesByRecordingIdsAsync(
            [recordingId],
            cancellationToken
        );
        return entries.SingleOrDefault();
    }

    private static async Task<RaidEncounterEntry?> FindRaidEncounterByRecordingIdAsync(
        RecordingCatalogRepository repository,
        Guid recordingId,
        CancellationToken cancellationToken
    )
    {
        var entries = await repository.ListRaidEncountersByRecordingIdsAsync(
            [recordingId],
            cancellationToken
        );
        return entries.SingleOrDefault();
    }

    private static RecordingCatalogSave CreateSave(Guid id, string filePath)
    {
        var startedAt = new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

        return new RecordingCatalogSave(
            id,
            filePath,
            RecordingCatalogStatus.Available,
            RecordingCatalogKind.Manual,
            startedAt,
            startedAt.AddMinutes(5),
            1,
            startedAt.AddMinutes(5)
        );
    }

    private static string WriteFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
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
            ConnectionFactory = connectionFactory;
            Repository = new RecordingCatalogRepository(connectionFactory);
        }

        public string DirectoryPath => _directory.Path;

        public SqliteConnectionFactory ConnectionFactory { get; }

        public RecordingCatalogRepository Repository { get; }

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
            Path = Directory.CreateTempSubdirectory("PullWatchCatalogTests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
