namespace PullWatch.Tests;

public sealed class RecordingStoragePresenterTests
{
    [Fact]
    public void StatusTextExplainsWhenFavoritesConstrainCapacity()
    {
        var presenter = new RecordingStoragePresenter(
            new RecordingStorageStatus(
                UsageBytes: 90,
                MaxUsageBytes: 100,
                RecordingCount: 2,
                IsRefreshing: false,
                IsCleaning: false,
                LastDeletedRecordingCount: 0,
                LastError: null,
                FavoriteUsageBytes: 90,
                FavoriteRecordingCount: 2
            )
        );

        var statusText = presenter.GetStatusText(isLimitEnabled: true, limitBytes: 100);

        Assert.Equal("Favourite recordings leave little room for new recordings.", statusText);
    }

    [Fact]
    public void StatusTextExplainsFavoriteRetentionPriority()
    {
        var presenter = new RecordingStoragePresenter(
            new RecordingStorageStatus(
                UsageBytes: 50,
                MaxUsageBytes: 100,
                RecordingCount: 2,
                IsRefreshing: false,
                IsCleaning: false,
                LastDeletedRecordingCount: 0,
                LastError: null,
                FavoriteUsageBytes: 20,
                FavoriteRecordingCount: 1
            )
        );

        var statusText = presenter.GetStatusText(isLimitEnabled: true, limitBytes: 100);

        Assert.Equal(
            "PullWatch keeps the newest recording, then removes older non-favourites before older favourites when the limit is reached.",
            statusText
        );
    }

    [Fact]
    public void StatusTextEvaluatesFavoritesAgainstConfiguredLimit()
    {
        var presenter = new RecordingStoragePresenter(
            new RecordingStorageStatus(
                UsageBytes: 90,
                MaxUsageBytes: 100,
                RecordingCount: 2,
                IsRefreshing: false,
                IsCleaning: false,
                LastDeletedRecordingCount: 0,
                LastError: null,
                FavoriteUsageBytes: 90,
                FavoriteRecordingCount: 2
            )
        );

        var statusText = presenter.GetStatusText(isLimitEnabled: true, limitBytes: 200);

        Assert.Equal(
            "PullWatch keeps the newest recording, then removes older non-favourites before older favourites when the limit is reached.",
            statusText
        );
    }
}
