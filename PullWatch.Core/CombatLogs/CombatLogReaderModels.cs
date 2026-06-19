namespace PullWatch;

public enum CombatLogReaderState
{
    WaitingForWow,
    WaitingForLogsDirectory,
    WaitingForCombatLog,
    ReadingCombatLog,
    SwitchingCombatLog,
}

public sealed record CombatLogReaderStatus(
    CombatLogReaderState State,
    string? CurrentPath,
    DateTimeOffset? LastSuccessfulReadTime,
    Exception? LastFileSystemError
);
