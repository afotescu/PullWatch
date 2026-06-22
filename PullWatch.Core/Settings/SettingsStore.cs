using System.Text.Json;

namespace PullWatch;

public enum SettingsLoadStatus
{
    Loaded,
    Missing,
    Invalid,
}

public sealed record SettingsLoadResult(
    SettingsLoadStatus Status,
    PullWatchSettings? Settings,
    Exception? Error = null
);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly Action<string, string> _replaceFile;

    public SettingsStore(string? settingsPath = null)
        : this(settingsPath ?? GetDefaultSettingsPath(), ReplaceFile) { }

    internal SettingsStore(string settingsPath, Action<string, string> replaceFile)
    {
        SettingsPath = settingsPath;
        _replaceFile = replaceFile;
    }

    public string SettingsPath { get; }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath))
        {
            return new SettingsLoadResult(SettingsLoadStatus.Missing, null);
        }

        try
        {
            await using var stream = new FileStream(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            var settings = await JsonSerializer.DeserializeAsync<PersistedPullWatchSettings>(
                stream,
                SerializerOptions,
                cancellationToken
            );

            return settings is null
                ? new SettingsLoadResult(
                    SettingsLoadStatus.Invalid,
                    null,
                    new JsonException("The settings file contained no settings object.")
                )
                : new SettingsLoadResult(SettingsLoadStatus.Loaded, settings.ToSettings());
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(SettingsLoadStatus.Invalid, null, exception);
        }
    }

    public async Task SaveAsync(PullWatchSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory =
            Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Settings path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp"
        );

        try
        {
            await using (
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough
                )
            )
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken
                );
                await stream.FlushAsync(cancellationToken);
            }

            _replaceFile(temporaryPath, SettingsPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed cleanup must not hide the save failure.
            }
        }
    }

    private static string GetDefaultSettingsPath()
    {
        return PullWatchDataPaths.SettingsPath;
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(sourcePath, destinationPath, null);
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    private sealed record PersistedPullWatchSettings
    {
        public int Version { get; init; } = PullWatchSettings.CurrentVersion;
        public string? WowLogsDirectory { get; init; }
        public string? RecordingsDirectory { get; init; }
        public bool RecordMythicPlus { get; init; } = true;
        public bool RecordRaidEncounters { get; init; } = true;
        public VideoSettings? Video { get; init; } = new();
        public AudioSettings? Audio { get; init; } = new();
        public StartupSettings? Startup { get; init; } = new();
        public PersistedUiSettings? Ui { get; init; } = new();

        public PullWatchSettings ToSettings()
        {
            return new PullWatchSettings
            {
                Version = Version,
                WowLogsDirectory = WowLogsDirectory,
                RecordingsDirectory = RecordingsDirectory,
                RecordMythicPlus = RecordMythicPlus,
                RecordRaidEncounters = RecordRaidEncounters,
                Video = Video ?? throw new JsonException("Video settings are required."),
                Audio = Audio ?? throw new JsonException("Audio settings are required."),
                Startup = Startup ?? throw new JsonException("Startup settings are required."),
                Ui = Ui?.ToSettings() ?? throw new JsonException("UI settings are required."),
            };
        }
    }

    private sealed record PersistedUiSettings
    {
        public WindowPlacementSettings? WindowPlacement { get; init; } = new();
        public bool SidebarCollapsed { get; init; }
        public RecordingListCategory SelectedRecordingCategory { get; init; } =
            RecordingListCategory.ChallengeMode;

        public UiSettings ToSettings()
        {
            return new UiSettings
            {
                WindowPlacement =
                    WindowPlacement
                    ?? throw new JsonException("Window placement settings are required."),
                SidebarCollapsed = SidebarCollapsed,
                SelectedRecordingCategory = SelectedRecordingCategory,
            };
        }
    }
}
