namespace PullWatch;

public enum PendingRecordingStorageLimitChangeAction
{
    Apply,
    Discard,
    Cancel,
}

public interface ISettingsDialogs
{
    string? PickFolder(string title, string? initialDirectory);

    PendingRecordingStorageLimitChangeAction ConfirmPendingRecordingStorageLimitChange(
        int currentGigabytes,
        int pendingGigabytes
    );
}
