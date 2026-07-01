using System.Windows.Input;

namespace PullWatch;

public sealed record NotificationContent(
    NotificationSeverity Severity,
    string Title,
    string Message,
    string? ActionText = null,
    ICommand? ActionCommand = null,
    bool IsDismissible = true,
    Action? Dismissed = null
);
