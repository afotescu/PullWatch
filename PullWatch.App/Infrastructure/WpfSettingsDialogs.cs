using System.Windows;
using Microsoft.Win32;

namespace PullWatch;

public sealed class WpfSettingsDialogs : ISettingsDialogs
{
    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory ?? string.Empty,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public PendingRecordingStorageLimitChangeAction ConfirmPendingRecordingStorageLimitChange(
        int currentGigabytes,
        int pendingGigabytes
    )
    {
        var result = WpfConfirmationDialog.Show(
            Application.Current?.MainWindow,
            new ConfirmationDialogRequest(
                "Unsaved storage limit",
                [
                    $"You changed the managed recordings storage limit from {currentGigabytes} GB to {pendingGigabytes} GB, but it has not been applied.",
                    "Applying the new limit may delete old PullWatch-managed recordings if current usage exceeds it.",
                ],
                [
                    new ConfirmationDialogButton(
                        "Apply",
                        ConfirmationDialogResult.Primary,
                        ConfirmationDialogButtonKind.Accent
                    ),
                    new ConfirmationDialogButton("Discard", ConfirmationDialogResult.Secondary),
                    new ConfirmationDialogButton(
                        "Cancel",
                        ConfirmationDialogResult.Cancel,
                        IsCancel: true
                    ),
                ]
            )
        );

        return result switch
        {
            ConfirmationDialogResult.Primary => PendingRecordingStorageLimitChangeAction.Apply,
            ConfirmationDialogResult.Secondary => PendingRecordingStorageLimitChangeAction.Discard,
            _ => PendingRecordingStorageLimitChangeAction.Cancel,
        };
    }
}
