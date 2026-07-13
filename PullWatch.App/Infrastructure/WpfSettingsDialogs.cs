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

    public bool ConfirmRecordingStorageCleanup(RecordingStorageCleanupConfirmation confirmation)
    {
        var recordingDescription =
            confirmation.RecordingCount == 1
                ? "1 PullWatch-managed recording"
                : $"{confirmation.RecordingCount} PullWatch-managed recordings";
        var result = WpfConfirmationDialog.Show(
            Application.Current?.MainWindow,
            new ConfirmationDialogRequest(
                "PullWatch",
                [
                    $"Your {recordingDescription} currently use {FileSizeFormatter.Format(confirmation.CurrentUsageBytes)}, which is more than the new {FileSizeFormatter.Format(confirmation.PendingLimitBytes)} limit.",
                    "Applying this limit will permanently delete the oldest recordings to reduce storage usage. Favourite recordings are deleted last.",
                ],
                [
                    new ConfirmationDialogButton(
                        "Apply and delete",
                        ConfirmationDialogResult.Primary,
                        ConfirmationDialogButtonKind.Destructive
                    ),
                    new ConfirmationDialogButton(
                        "Cancel",
                        ConfirmationDialogResult.Cancel,
                        IsCancel: true
                    ),
                ],
                Heading: "Delete old recordings?"
            )
        );

        return result == ConfirmationDialogResult.Primary;
    }

    public PendingRecordingStorageLimitChangeAction ConfirmPendingRecordingStorageLimitChange(
        PendingRecordingStorageLimitChange change
    )
    {
        var changeDescription =
            change.CurrentIsEnabled && !change.PendingIsEnabled
                ? "You disabled the managed recordings storage limit, but the change has not been applied."
            : !change.CurrentIsEnabled && change.PendingIsEnabled
                ? $"You enabled the managed recordings storage limit at {change.PendingGigabytes} GB, but it has not been applied."
            : $"You changed the managed recordings storage limit from {change.CurrentGigabytes} GB to {change.PendingGigabytes} GB, but it has not been applied.";
        var result = WpfConfirmationDialog.Show(
            Application.Current?.MainWindow,
            new ConfirmationDialogRequest(
                "PullWatch",
                [
                    changeDescription,
                    change.PendingIsEnabled
                        ? "Applying the new limit may delete old PullWatch-managed recordings if current usage exceeds it."
                        : "Applying this change stops automatic storage cleanup.",
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
                ],
                Heading: "Apply storage limit change?"
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
