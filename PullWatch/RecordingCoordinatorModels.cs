namespace PullWatch;

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
    RecordingOwner? SuppressedUntilOwnerEnd,
    string? SuppressedIdentity,
    Exception? LastFailure);

public sealed class RecordingServiceFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
