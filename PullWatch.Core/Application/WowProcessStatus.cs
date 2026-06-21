namespace PullWatch;

public enum WowProcessState
{
    WaitingForProcess,
    WaitingForWindow,
    WindowAvailable,
}

public sealed record WowProcessStatus(
    WowProcessState State,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    string? MainWindowTitle,
    Exception? LastError
)
{
    public bool IsWindowAvailable => State == WowProcessState.WindowAvailable && LastError is null;
}
