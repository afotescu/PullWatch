namespace PullWatch;

public enum PendingRecordingStorageLimitChangeAction
{
    Apply,
    Discard,
    Cancel,
}

public sealed record PendingRecordingStorageLimitChange(
    bool CurrentIsEnabled,
    int CurrentGigabytes,
    bool PendingIsEnabled,
    int PendingGigabytes
);

public interface ISettingsDialogs
{
    string? PickFolder(string title, string? initialDirectory);

    PendingRecordingStorageLimitChangeAction ConfirmPendingRecordingStorageLimitChange(
        PendingRecordingStorageLimitChange change
    );
}
