namespace PullWatch;

public sealed class RecordingCatalog(RecordingCatalogRepository repository)
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly RecordingCatalogRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<Guid> BeginRecordingAsync(
        RecordingContext context,
        string outputPath,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var id = Guid.NewGuid();
        await _repository.UpsertAsync(
            new RecordingCatalogSave(
                id,
                NormalizeFilePath(outputPath),
                RecordingCatalogStatus.Recording,
                GetKind(context),
                context.StartedAt.ToUniversalTime(),
                null,
                null,
                null
            ),
            CreateRaidEncounterSave(id, context),
            cancellationToken
        );

        return id;
    }

    public async Task CompleteRecordingAsync(
        Guid id,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken
    )
    {
        await CompleteRecordingAsync(id, endedAtUtc, null, cancellationToken);
    }

    public async Task CompleteRecordingAsync(
        Guid id,
        DateTimeOffset endedAtUtc,
        EncounterRecordingEnd? encounterEnd,
        CancellationToken cancellationToken
    )
    {
        var entry = await _repository.GetByIdAsync(id, cancellationToken);

        if (entry is null)
        {
            return;
        }

        var file = new FileInfo(entry.FilePath);

        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The completed recording file was not found.",
                entry.FilePath
            );
        }

        await _repository.UpdateAsync(
            new RecordingCatalogSave(
                entry.Id,
                NormalizeFilePath(file.FullName),
                RecordingCatalogStatus.Available,
                entry.Kind,
                entry.StartedAtUtc,
                endedAtUtc.ToUniversalTime(),
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc)
            ),
            CreateRaidEncounterCompletionSave(entry, encounterEnd),
            cancellationToken
        );
    }

    public async Task RemoveRecordingAsync(Guid id, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(id, cancellationToken);
    }

    public async Task DeleteAvailableRecordingAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await _repository.GetByIdAsync(id, cancellationToken);

        if (entry is null)
        {
            return;
        }

        if (entry.Status != RecordingCatalogStatus.Available)
        {
            throw new InvalidOperationException("Only finished recordings can be deleted.");
        }

        var normalizedFilePath = TryNormalizeFilePath(entry.FilePath);

        if (normalizedFilePath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(normalizedFilePath);
        }

        await _repository.DeleteAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<RecordingCatalogFile>> ListAvailableFilesAsync(
        string recordingsDirectory,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsDirectory);

        var normalizedDirectory = NormalizeDirectoryPath(recordingsDirectory);
        var recordings = new List<RecordingCatalogFile>();
        var entries = await _repository.ListAsync(cancellationToken);

        foreach (var entry in entries)
        {
            if (entry.Status != RecordingCatalogStatus.Available)
            {
                continue;
            }

            var normalizedFilePath = TryNormalizeFilePath(entry.FilePath);

            if (normalizedFilePath is null)
            {
                continue;
            }

            var file = new FileInfo(normalizedFilePath);

            if (!file.Exists)
            {
                await _repository.DeleteAsync(entry.Id, cancellationToken);
                continue;
            }

            if (!IsTopLevelFileInDirectory(normalizedFilePath, normalizedDirectory))
            {
                continue;
            }

            recordings.Add(
                new RecordingCatalogFile(
                    entry.Id,
                    NormalizeFilePath(file.FullName),
                    entry.Kind,
                    entry.StartedAtUtc,
                    entry.EndedAtUtc,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc)
                )
            );
        }

        return recordings
            .OrderByDescending(recording => recording.ModifiedAtUtc)
            .ThenBy(
                recording => Path.GetFileName(recording.FilePath),
                StringComparer.OrdinalIgnoreCase
            )
            .ToList();
    }

    private static RecordingCatalogKind GetKind(RecordingContext context)
    {
        return context switch
        {
            ManualRecordingContext => RecordingCatalogKind.Manual,
            ChallengeRecordingContext => RecordingCatalogKind.ChallengeMode,
            EncounterRecordingContext => RecordingCatalogKind.Encounter,
            _ => throw new ArgumentOutOfRangeException(
                nameof(context),
                context,
                "Unknown recording context."
            ),
        };
    }

    private static RaidEncounterSave? CreateRaidEncounterSave(
        Guid recordingId,
        RecordingContext context
    )
    {
        return context is EncounterRecordingContext encounter
            ? new RaidEncounterSave(
                recordingId,
                encounter.EncounterId,
                encounter.EncounterName,
                encounter.DifficultyId,
                encounter.GroupSize,
                encounter.InstanceId,
                encounter.StartedAt.ToUniversalTime(),
                RaidEncounterOutcome.Unknown,
                null,
                null
            )
            : null;
    }

    private static RaidEncounterCompletionSave? CreateRaidEncounterCompletionSave(
        RecordingCatalogEntry entry,
        EncounterRecordingEnd? encounterEnd
    )
    {
        return entry.Kind == RecordingCatalogKind.Encounter && encounterEnd is not null
            ? new RaidEncounterCompletionSave(
                entry.Id,
                encounterEnd.Outcome,
                encounterEnd.EndedAt.ToUniversalTime(),
                encounterEnd.DurationMilliseconds
            )
            : null;
    }

    private static bool IsTopLevelFileInDirectory(string filePath, string recordingsDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(filePath);
        return parentDirectory is not null
            && PathComparer.Equals(parentDirectory, recordingsDirectory);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);

        if (root is not null && PathComparer.Equals(fullPath, root))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeFilePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static string? TryNormalizeFilePath(string path)
    {
        try
        {
            return NormalizeFilePath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
