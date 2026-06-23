namespace PullWatch;

public abstract record RecordingContext(DateTimeOffset StartedAt);

public sealed record ManualRecordingContext(DateTimeOffset StartedAt) : RecordingContext(StartedAt);

public sealed record ChallengeRecordingContext(
    DateTimeOffset StartedAt,
    string DungeonName,
    int MapId,
    int ChallengeModeId,
    int KeystoneLevel,
    IReadOnlyList<int> AffixIds
) : RecordingContext(StartedAt);

public abstract record RecordingActivityEnd(DateTimeOffset EndedAt);

public sealed record EncounterRecordingContext(
    DateTimeOffset StartedAt,
    int EncounterId,
    string EncounterName,
    int DifficultyId,
    int? GroupSize = null,
    int? InstanceId = null
) : RecordingContext(StartedAt);

public sealed record EncounterRecordingEnd(
    DateTimeOffset EndedAt,
    int EncounterId,
    string EncounterName,
    int DifficultyId,
    int? GroupSize,
    RaidEncounterOutcome Outcome,
    int? DurationMilliseconds
) : RecordingActivityEnd(EndedAt);

public sealed record ChallengeRecordingEnd(
    DateTimeOffset EndedAt,
    int MapId,
    ChallengeModeOutcome Outcome,
    int KeystoneLevel,
    int? TotalTimeMilliseconds,
    double? OnTimeSeconds,
    int? TimerLimitSeconds
) : RecordingActivityEnd(EndedAt);

public sealed record ZoneChangeContext(
    DateTimeOffset ChangedAt,
    int ZoneId,
    string ZoneName,
    int InstanceType
);

public sealed record MapChangeContext(DateTimeOffset ChangedAt, int UiMapId, string MapName);

public enum RecordingOwner
{
    Manual,
    ChallengeMode,
    Encounter,
}

public enum RaidEncounterOutcome
{
    Unknown,
    Wipe,
    Kill,
}

public enum ChallengeModeOutcome
{
    Unknown,
    Depleted,
    Timed,
}

public enum RecordingCoordinatorState
{
    Idle,
    Starting,
    Recording,
    Stopping,
}

public enum RecordingCommandResult
{
    Started,
    Stopped,
    AlreadyActive,
    NoActiveRecording,
    OwnerMismatch,
    Suppressed,
    TargetUnavailable,
    Failed,
    TimedOut,
}

public sealed record RecordingCoordinatorStatus(
    RecordingCoordinatorState State,
    RecordingOwner? Owner,
    string? Identity,
    RecordingContext? Context,
    RecordingOwner? SuppressedUntilOwnerEnd,
    string? SuppressedIdentity,
    Exception? LastFailure,
    string? ActiveOutputPath
)
{
    public RecordingStatistics Statistics { get; init; } = new(0, 0);
}

public sealed record RecordingStatistics(int ExpectedCount, int SavedCount);

public sealed class RecordingServiceFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
