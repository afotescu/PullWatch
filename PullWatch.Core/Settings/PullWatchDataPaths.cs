namespace PullWatch;

public static class PullWatchDataPaths
{
    private const string ApplicationDirectoryName = "PullWatch";
    private const string SettingsFileName = "settings.json";
    private const string DatabaseFileName = "pullwatch.db";

    public static string LocalApplicationDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationDirectoryName
        );

    public static string SettingsPath =>
        Path.Combine(LocalApplicationDataDirectory, SettingsFileName);

    public static string DatabasePath =>
        Path.Combine(LocalApplicationDataDirectory, DatabaseFileName);
}
