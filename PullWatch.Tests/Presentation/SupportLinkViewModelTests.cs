using System.ComponentModel;

namespace PullWatch.Tests;

public sealed class SupportLinkViewModelTests
{
    private static readonly Uri InviteUri = new("https://discord.gg/pullwatch-test");

    [Fact]
    public void OpenDiscordCommandLaunchesConfiguredInviteOnce()
    {
        var launcher = new FakeExternalLinkLauncher();
        var notifications = new NotificationCenterViewModel();
        var viewModel = new SupportLinkViewModel(launcher, notifications, InviteUri);

        viewModel.OpenDiscordCommand.Execute(null);

        Assert.True(viewModel.IsAvailable);
        Assert.Equal(InviteUri, Assert.Single(launcher.OpenedUris));
    }

    [Fact]
    public void SuccessfulLaunchDoesNotShowNotification()
    {
        var launcher = new FakeExternalLinkLauncher();
        var notifications = new NotificationCenterViewModel();
        var viewModel = new SupportLinkViewModel(launcher, notifications, InviteUri);

        viewModel.OpenDiscordCommand.Execute(null);

        Assert.Empty(notifications.Items);
        Assert.False(notifications.HasNotifications);
    }

    [Fact]
    public void UnconfiguredInviteIsUnavailableAndDoesNotLaunch()
    {
        var launcher = new FakeExternalLinkLauncher();
        var notifications = new NotificationCenterViewModel();
        var viewModel = new SupportLinkViewModel(launcher, notifications, inviteUri: null);

        viewModel.OpenDiscordCommand.Execute(null);

        Assert.False(viewModel.IsAvailable);
        Assert.Empty(launcher.OpenedUris);
        Assert.Empty(notifications.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaunchFailureShowsDismissibleErrorNotification(bool useWin32Exception)
    {
        var launcher = new FakeExternalLinkLauncher
        {
            Failure = useWin32Exception
                ? new Win32Exception("No application is associated with this link.")
                : new InvalidOperationException("The shell could not start the process."),
        };
        var notifications = new NotificationCenterViewModel();
        var viewModel = new SupportLinkViewModel(launcher, notifications, InviteUri);

        viewModel.OpenDiscordCommand.Execute(null);

        var notification = Assert.Single(notifications.Items);
        Assert.Equal("discord-support-open-failed", notification.Id);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.True(notification.IsDismissible);
        Assert.Contains("default browser", notification.Message);
    }

    [Fact]
    public void RepeatedLaunchFailuresReuseTheSameNotification()
    {
        var launcher = new FakeExternalLinkLauncher { Failure = new Win32Exception("No handler.") };
        var notifications = new NotificationCenterViewModel();
        var viewModel = new SupportLinkViewModel(launcher, notifications, InviteUri);

        viewModel.OpenDiscordCommand.Execute(null);
        viewModel.OpenDiscordCommand.Execute(null);

        Assert.Equal(2, launcher.OpenedUris.Count);
        Assert.Single(notifications.Items);
    }

    private sealed class FakeExternalLinkLauncher : IExternalLinkLauncher
    {
        public List<Uri> OpenedUris { get; } = [];

        public Exception? Failure { get; init; }

        public void Open(Uri uri)
        {
            OpenedUris.Add(uri);

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
