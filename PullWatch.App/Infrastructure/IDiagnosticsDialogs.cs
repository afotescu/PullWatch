namespace PullWatch;

public interface IDiagnosticsDialogs
{
    void CopyText(string text);

    Task<string?> PickDiagnosticsExportPathAsync(string suggestedFileName);

    Task WriteTextAsync(string path, string text, CancellationToken cancellationToken);
}
