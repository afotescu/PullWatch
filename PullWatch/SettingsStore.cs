using System.Text.Json;

namespace PullWatch;

public enum SettingsLoadStatus
{
    Loaded,
    Missing,
    Invalid
}

public sealed record SettingsLoadResult(
    SettingsLoadStatus Status,
    PullWatchSettings? Settings,
    Exception? Error = null);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly Action<string, string> _replaceFile;

    public SettingsStore(string? settingsPath = null)
        : this(settingsPath ?? GetDefaultSettingsPath(), ReplaceFile)
    {
    }

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
                FileShare.Read);
            var settings = await JsonSerializer.DeserializeAsync<PullWatchSettings>(
                stream,
                SerializerOptions,
                cancellationToken);

            return settings is null
                ? new SettingsLoadResult(
                    SettingsLoadStatus.Invalid,
                    null,
                    new JsonException("The settings file contained no settings object."))
                : new SettingsLoadResult(SettingsLoadStatus.Loaded, settings);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(SettingsLoadStatus.Invalid, null, exception);
        }
    }

    public async Task SaveAsync(PullWatchSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Settings path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken);
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
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PullWatch",
            "settings.json");
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
}
