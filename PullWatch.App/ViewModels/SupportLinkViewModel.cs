using System.ComponentModel;

namespace PullWatch;

public sealed partial class SupportLinkViewModel : ObservableObject
{
    private const string OpenFailedNotificationId = "discord-support-open-failed";

    // TODO: Replace with the permanent PullWatch Discord invite before release.
    private const string DiscordInviteUrl = "";

    private readonly IExternalLinkLauncher _launcher;
    private readonly NotificationCenterViewModel _notifications;
    private readonly Uri? _inviteUri;

    public SupportLinkViewModel(
        IExternalLinkLauncher launcher,
        NotificationCenterViewModel notifications
    )
        : this(launcher, notifications, ParseConfiguredInvite()) { }

    internal SupportLinkViewModel(
        IExternalLinkLauncher launcher,
        NotificationCenterViewModel notifications,
        Uri? inviteUri
    )
    {
        _launcher = launcher;
        _notifications = notifications;
        _inviteUri = inviteUri;
    }

    public bool IsAvailable => _inviteUri is not null;

    public string ToolTip => "Open PullWatch support on Discord";

    private static Uri? ParseConfiguredInvite()
    {
        return string.IsNullOrWhiteSpace(DiscordInviteUrl) ? null : new Uri(DiscordInviteUrl);
    }

    [RelayCommand]
    private void OpenDiscord()
    {
        if (_inviteUri is null)
        {
            return;
        }

        try
        {
            _launcher.Open(_inviteUri);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _notifications.ShowOrUpdate(
                OpenFailedNotificationId,
                new NotificationContent(
                    NotificationSeverity.Error,
                    "Couldn't open Discord",
                    "PullWatch couldn't open Discord in your default browser. Check that a default browser is configured and try again."
                )
            );
        }
    }
}
