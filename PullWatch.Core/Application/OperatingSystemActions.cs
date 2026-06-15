using System.Diagnostics;

namespace PullWatch;

public sealed class OperatingSystemActions(SettingsProvider settingsProvider)
    : IOperatingSystemActions
{
    public Task OpenRecordingsFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = settingsProvider.Current.RecordingsDirectory
            ?? throw new InvalidOperationException("Recordings directory was not configured.");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
