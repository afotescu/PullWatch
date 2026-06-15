namespace PullWatch;

public abstract record RecordingContext(DateTimeOffset StartedAt);

public sealed record ManualRecordingContext(DateTimeOffset StartedAt)
    : RecordingContext(StartedAt);

public sealed record ChallengeRecordingContext(
    DateTimeOffset StartedAt,
    string DungeonName,
    int Level)
    : RecordingContext(StartedAt);

public sealed record EncounterRecordingContext(
    DateTimeOffset StartedAt,
    int EncounterId,
    string EncounterName,
    int DifficultyId)
    : RecordingContext(StartedAt);

public enum RecordingOwner
{
    Manual,
    ChallengeMode,
    Encounter
}

public enum RecordingCoordinatorState
{
    Idle,
    Starting,
    Recording,
    Stopping
}

public enum RecordingCommandResult
{
    Started,
    Stopped,
    AlreadyActive,
    NoActiveRecording,
    OwnerMismatch,
    Suppressed,
    Failed,
    TimedOut
}

public sealed record RecordingCoordinatorStatus(
    RecordingCoordinatorState State,
    RecordingOwner? Owner,
    string? Identity,
    RecordingContext? Context,
    RecordingOwner? SuppressedUntilOwnerEnd,
    string? SuppressedIdentity,
    Exception? LastFailure,
    string? ActiveOutputPath)
{
    public RecordingStatistics Statistics { get; init; } = new(0, 0);
}

public sealed record RecordingStatistics(
    int ExpectedCount,
    int SavedCount);

public sealed class RecordingServiceFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
