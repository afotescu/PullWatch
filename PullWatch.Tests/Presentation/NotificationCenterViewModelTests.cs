namespace PullWatch.Tests;

public sealed class NotificationCenterViewModelTests
{
    [Fact]
    public void ShowOrUpdateReusesNotificationWithSameId()
    {
        var center = new NotificationCenterViewModel();

        var first = center.ShowOrUpdate(
            "update",
            new NotificationContent(
                NotificationSeverity.Information,
                "Update available",
                "Version 1.2.3 is available."
            )
        );
        var second = center.ShowOrUpdate(
            "update",
            new NotificationContent(
                NotificationSeverity.Success,
                "Update ready",
                "Restart PullWatch to install version 1.2.3."
            )
        );

        Assert.Same(first, second);
        Assert.Single(center.Items);
        Assert.True(center.HasNotifications);
        Assert.Equal(NotificationSeverity.Success, first.Severity);
        Assert.Equal("Update ready", first.Title);
    }

    [Fact]
    public void DismissCommandRemovesDismissibleNotification()
    {
        var center = new NotificationCenterViewModel();
        var notification = center.ShowOrUpdate(
            "notice",
            new NotificationContent(
                NotificationSeverity.Information,
                "Notice",
                "This can be dismissed."
            )
        );

        notification.DismissCommand.Execute(null);

        Assert.Empty(center.Items);
        Assert.False(center.HasNotifications);
    }

    [Fact]
    public void DismissCommandRunsDismissedCallback()
    {
        var center = new NotificationCenterViewModel();
        var dismissed = false;
        var notification = center.ShowOrUpdate(
            "notice",
            new NotificationContent(
                NotificationSeverity.Information,
                "Notice",
                "This can be dismissed.",
                Dismissed: () => dismissed = true
            )
        );

        notification.DismissCommand.Execute(null);

        Assert.True(dismissed);
    }
}
