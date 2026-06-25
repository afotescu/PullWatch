namespace PullWatch;

public sealed record RecordingStorageStatus(
    long? UsageBytes,
    long MaxUsageBytes,
    int RecordingCount,
    bool IsRefreshing,
    bool IsCleaning,
    int LastDeletedRecordingCount,
    Exception? LastError
)
{
    public static RecordingStorageStatus Initial { get; } =
        new(
            null,
            RecordingStorageSettings.DefaultMaxUsageBytes,
            0,
            IsRefreshing: false,
            IsCleaning: false,
            LastDeletedRecordingCount: 0,
            LastError: null
        );

    public bool IsLimitEnabled => MaxUsageBytes > RecordingStorageSettings.UnlimitedBytes;
}
