namespace PullWatch.Tests;

public sealed class FavoriteStorageWarningTrackerTests
{
    [Fact]
    public void CrossingSixtyAndEightyPercentQueuesEachWarningOnce()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 59));

        Assert.Null(tracker.TakePendingWarning());
        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Advisory60,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );

        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 79)));
        Assert.Null(tracker.TakePendingWarning());

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 80)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Warning80,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );

        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 90)));
        Assert.Null(tracker.TakePendingWarning());
    }

    [Fact]
    public void JumpingDirectlyToEightyPercentQueuesOnlyHighestWarning()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 59));

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 85)));

        var warning = Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning());
        Assert.Equal(FavoriteStorageWarningLevel.Warning80, warning.Level);
        Assert.Equal(85, warning.FavoriteUsageBytes);
        Assert.Equal(100, warning.MaxUsageBytes);
    }

    [Fact]
    public void DroppingBelowThresholdsRearmsTheirWarnings()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 59));

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60)));
        tracker.TakePendingWarning();
        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 80)));
        tracker.TakePendingWarning();

        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 75)));
        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 80)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Warning80,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );

        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 55)));
        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Advisory60,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );
    }

    [Fact]
    public void TransitionalAndFailedStatusesDoNotChangeThresholdState()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 59));

        Assert.False(
            tracker.ApplyStatus(Status(favoriteUsageBytes: 85) with { IsRefreshing = true })
        );
        Assert.False(
            tracker.ApplyStatus(Status(favoriteUsageBytes: 85) with { IsCleaning = true })
        );
        Assert.False(
            tracker.ApplyStatus(
                Status(favoriteUsageBytes: 85) with
                {
                    LastError = new IOException("Storage scan failed."),
                }
            )
        );
        Assert.Null(tracker.TakePendingWarning());

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Advisory60,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );
    }

    [Fact]
    public void DisablingLimitRearmsAdvisoryWarning()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 59));

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60)));
        tracker.TakePendingWarning();

        Assert.False(
            tracker.ApplyStatus(
                Status(
                    favoriteUsageBytes: 60,
                    maxUsageBytes: RecordingStorageSettings.UnlimitedBytes
                )
            )
        );
        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Advisory60,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );
    }

    [Fact]
    public void PendingWarningTracksCurrentLevelUntilItIsPresented()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 59));

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 85)));
        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 75)));

        Assert.Equal(
            FavoriteStorageWarningLevel.Advisory60,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );

        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 85)));
        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 55)));
        Assert.Null(tracker.TakePendingWarning());
    }

    [Fact]
    public void InitialReliableStatusEstablishesBaselineWithoutWarning()
    {
        var tracker = new FavoriteStorageWarningTracker(Status(favoriteUsageBytes: 85));

        Assert.Null(tracker.TakePendingWarning());
        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 90)));
        Assert.Null(tracker.TakePendingWarning());
    }

    [Fact]
    public void FirstReliableStatusAfterStartupEstablishesBaselineWithoutWarning()
    {
        var tracker = new FavoriteStorageWarningTracker(RecordingStorageStatus.Initial);

        Assert.False(tracker.ApplyStatus(Status(favoriteUsageBytes: 85)));
        Assert.Null(tracker.TakePendingWarning());
    }

    [Fact]
    public void ThresholdUsesFavoriteUsageInsteadOfTotalUsage()
    {
        var tracker = new FavoriteStorageWarningTracker(
            Status(favoriteUsageBytes: 59, usageBytes: 95)
        );

        Assert.Null(tracker.TakePendingWarning());
        Assert.True(tracker.ApplyStatus(Status(favoriteUsageBytes: 60, usageBytes: 95)));
        Assert.Equal(
            FavoriteStorageWarningLevel.Advisory60,
            Assert.IsType<FavoriteStorageWarning>(tracker.TakePendingWarning()).Level
        );
    }

    private static RecordingStorageStatus Status(
        long favoriteUsageBytes,
        long usageBytes = 95,
        long maxUsageBytes = 100
    )
    {
        return new RecordingStorageStatus(
            UsageBytes: usageBytes,
            MaxUsageBytes: maxUsageBytes,
            RecordingCount: 3,
            IsRefreshing: false,
            IsCleaning: false,
            LastDeletedRecordingCount: 0,
            LastError: null,
            FavoriteUsageBytes: favoriteUsageBytes,
            FavoriteRecordingCount: 2
        );
    }
}
