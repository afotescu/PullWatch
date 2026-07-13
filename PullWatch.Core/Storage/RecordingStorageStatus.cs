namespace PullWatch;

public sealed record RecordingStorageStatus(
    long? UsageBytes,
    long MaxUsageBytes,
    int RecordingCount,
    bool IsRefreshing,
    bool IsCleaning,
    int LastDeletedRecordingCount,
    Exception? LastError,
    long FavoriteUsageBytes = 0,
    int FavoriteRecordingCount = 0
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

    public bool IsFavoriteCapacityConstrained => IsFavoriteCapacityConstrainedFor(MaxUsageBytes);

    public bool IsFavoriteCapacityConstrainedFor(long maxUsageBytes) =>
        maxUsageBytes > RecordingStorageSettings.UnlimitedBytes
        && FavoriteUsageBytes > 0
        && FavoriteUsageBytes
            >= RecordingStorageRetentionPolicy.GetCleanupTargetBytes(maxUsageBytes);
}

internal static class RecordingStorageRetentionPolicy
{
    public static long GetCleanupTargetBytes(long maxUsageBytes)
    {
        var cleanupMarginBytes = Math.Max(1, maxUsageBytes / 10);
        return Math.Max(0, maxUsageBytes - cleanupMarginBytes);
    }
}
