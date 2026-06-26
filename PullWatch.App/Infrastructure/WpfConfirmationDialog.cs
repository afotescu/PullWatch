using System.Windows;

namespace PullWatch;

public static class WpfConfirmationDialog
{
    public static ConfirmationDialogResult Show(Window? owner, ConfirmationDialogRequest request)
    {
        var dialog = new ConfirmationDialogWindow(request);

        if (owner?.IsVisible == true)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}
