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
            InitialDirectory = initialDirectory ?? string.Empty
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool SaveBeforeLeavingSettings()
    {
        return MessageBox.Show(
                   "Save your settings changes before leaving?",
                   "Unsaved settings",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
