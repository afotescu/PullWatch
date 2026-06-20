namespace PullWatch;

public sealed class WpfDiagnosticsDialogs : IDiagnosticsDialogs
{
    public void CopyText(string text)
    {
        System.Windows.Clipboard.SetText(text);
    }

    public Task<string?> PickDiagnosticsExportPathAsync(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PullWatch diagnostics",
            FileName = suggestedFileName,
            DefaultExt = ".txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task WriteTextAsync(string path, string text)
    {
        return System.IO.File.WriteAllTextAsync(path, text);
    }
}
