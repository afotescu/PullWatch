namespace PullWatch;

public sealed class RecordingDatabasePathProvider(string? databasePath = null)
{
    public string DatabasePath { get; } =
        string.IsNullOrWhiteSpace(databasePath) ? PullWatchDataPaths.DatabasePath : databasePath;
}
