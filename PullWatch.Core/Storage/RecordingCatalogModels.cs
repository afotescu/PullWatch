namespace PullWatch;

public enum RecordingCatalogStatus
{
    Recording,
    Available,
}

public enum RecordingCatalogKind
{
    Unknown,
    Manual,
    ChallengeMode,
    Encounter,
}

public sealed record RecordingCatalogEntry(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string FilePath,
    RecordingCatalogStatus Status,
    RecordingCatalogKind Kind,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long? FileSizeBytes,
    DateTimeOffset? FileModifiedAtUtc
);

public sealed record RecordingCatalogSave(
    Guid Id,
    string FilePath,
    RecordingCatalogStatus Status,
    RecordingCatalogKind Kind,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long? FileSizeBytes,
    DateTimeOffset? FileModifiedAtUtc
);

public sealed record RecordingCatalogFile(
    Guid Id,
    string FilePath,
    RecordingCatalogKind Kind,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long SizeBytes,
    DateTimeOffset ModifiedAtUtc
);
