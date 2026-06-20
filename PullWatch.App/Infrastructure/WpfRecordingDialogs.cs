using System.Windows;

namespace PullWatch;

public sealed class WpfRecordingDialogs : IRecordingDialogs
{
    public bool ConfirmPermanentDelete(RecordingListItem recording)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(
                "Are you sure? This action is permanent.",
                "Delete recording",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            )
            : MessageBox.Show(
                owner,
                "Are you sure? This action is permanent.",
                "Delete recording",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

        return result == MessageBoxResult.Yes;
    }
}
