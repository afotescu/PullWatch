namespace PullWatch;

public enum ConfirmationDialogResult
{
    Primary,
    Secondary,
    Cancel,
}

public enum ConfirmationDialogButtonKind
{
    Normal,
    Accent,
    Destructive,
}

public sealed record ConfirmationDialogRequest(
    string Title,
    IReadOnlyList<string> MessageLines,
    IReadOnlyList<ConfirmationDialogButton> Buttons,
    ConfirmationDialogResult DefaultResult = ConfirmationDialogResult.Cancel
);

public sealed record ConfirmationDialogButton(
    string Text,
    ConfirmationDialogResult Result,
    ConfirmationDialogButtonKind Kind = ConfirmationDialogButtonKind.Normal,
    bool IsDefault = false,
    bool IsCancel = false
);
