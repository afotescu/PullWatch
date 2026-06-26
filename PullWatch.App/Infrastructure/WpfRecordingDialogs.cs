using System.Windows;

namespace PullWatch;

public sealed class WpfRecordingDialogs : IRecordingDialogs
{
    public bool ConfirmPermanentDelete(RecordingListItem recording)
    {
        var result = WpfConfirmationDialog.Show(
            Application.Current?.MainWindow,
            new ConfirmationDialogRequest(
                "Delete recording",
                [$"Delete '{recording.DisplayName}' permanently?", "This action cannot be undone."],
                [
                    new ConfirmationDialogButton(
                        "Delete",
                        ConfirmationDialogResult.Primary,
                        ConfirmationDialogButtonKind.Destructive
                    ),
                    new ConfirmationDialogButton(
                        "Cancel",
                        ConfirmationDialogResult.Cancel,
                        IsCancel: true
                    ),
                ]
            )
        );

        return result == ConfirmationDialogResult.Primary;
    }
}
