namespace PullWatch;

public sealed record ApplicationStatus(
    PullWatchSettings? EffectiveSettings,
    RecordingCoordinatorStatus Recording,
    CombatLogReaderStatus CombatLog,
    WowProcessStatus WowProcess);
