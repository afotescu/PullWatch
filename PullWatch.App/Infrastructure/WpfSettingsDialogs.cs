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
}
