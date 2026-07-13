namespace PullWatch;

internal enum FavoriteStorageWarningLevel
{
    None,
    Advisory60,
    Warning80,
}

internal sealed record FavoriteStorageWarning(
    FavoriteStorageWarningLevel Level,
    long FavoriteUsageBytes,
    long MaxUsageBytes
);

internal sealed class FavoriteStorageWarningTracker
{
    private FavoriteStorageWarningLevel? _currentLevel;
    private FavoriteStorageWarning? _pendingWarning;

    public FavoriteStorageWarningTracker(RecordingStorageStatus initialStatus)
    {
        ApplyStatus(initialStatus);
    }

    public bool ApplyStatus(RecordingStorageStatus status)
    {
        if (!IsReliable(status))
        {
            return false;
        }

        var nextLevel = GetLevel(status);

        if (_currentLevel is not { } currentLevel)
        {
            _currentLevel = nextLevel;
            return false;
        }

        var crossedUpward = nextLevel > currentLevel;

        if (crossedUpward || _pendingWarning is not null)
        {
            _pendingWarning = CreateWarning(nextLevel, status);
        }

        _currentLevel = nextLevel;
        return crossedUpward;
    }

    public FavoriteStorageWarning? TakePendingWarning()
    {
        var warning = _pendingWarning;
        _pendingWarning = null;
        return warning;
    }

    private static bool IsReliable(RecordingStorageStatus status)
    {
        return status.UsageBytes is not null
            && !status.IsRefreshing
            && !status.IsCleaning
            && status.LastError is null;
    }

    private static FavoriteStorageWarningLevel GetLevel(RecordingStorageStatus status)
    {
        if (!status.IsLimitEnabled || status.FavoriteUsageBytes <= 0)
        {
            return FavoriteStorageWarningLevel.None;
        }

        var favoriteUsageRatio = (decimal)status.FavoriteUsageBytes / status.MaxUsageBytes;

        if (favoriteUsageRatio >= 0.8m)
        {
            return FavoriteStorageWarningLevel.Warning80;
        }

        return favoriteUsageRatio >= 0.6m
            ? FavoriteStorageWarningLevel.Advisory60
            : FavoriteStorageWarningLevel.None;
    }

    private static FavoriteStorageWarning? CreateWarning(
        FavoriteStorageWarningLevel level,
        RecordingStorageStatus status
    )
    {
        return level == FavoriteStorageWarningLevel.None
            ? null
            : new FavoriteStorageWarning(level, status.FavoriteUsageBytes, status.MaxUsageBytes);
    }
}
