namespace PullWatch;

public enum RecordingStatusHealth
{
    Idle,
    Waiting,
    ManualOnly,
    Ready,
    Active,
    AttentionNeeded,
}

public enum RecorderPresentationState
{
    Idle,
    Ready,
    Starting,
    Recording,
    Stopping,
    Error,
}
