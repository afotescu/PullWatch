namespace PullWatch;

public sealed record SettingsValidationResult(
    PullWatchSettings? Settings,
    IReadOnlyList<string> Errors
)
{
    public bool IsValid => Settings is not null;
}

public static class SettingsValidator
{
    public static SettingsValidationResult Validate(PullWatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();

        if (settings.Version != PullWatchSettings.CurrentVersion)
        {
            errors.Add(
                $"Settings version must be {PullWatchSettings.CurrentVersion}, but was {settings.Version}."
            );
        }

        if (settings.Video is null)
        {
            errors.Add("Video settings are required.");
        }
        else
        {
            if (!VideoFrameRates.IsSupported(settings.Video.FrameRate))
            {
                errors.Add(
                    $"Video frame rate must be {VideoFrameRates.Standard} or {VideoFrameRates.High}."
                );
            }

            if (!Enum.IsDefined(settings.Video.Quality))
            {
                errors.Add("Video quality must be Compact, Balanced, or High.");
            }
        }

        if (settings.Audio is null)
        {
            errors.Add("Audio settings are required.");
        }

        if (settings.Ui is null)
        {
            errors.Add("UI settings are required.");
        }
        else if (settings.Ui.WindowPlacement is null)
        {
            errors.Add("Window placement settings are required.");
        }

        var wowLogsDirectory = NormalizeOptionalPath(
            settings.WowLogsDirectory,
            "WoW logs directory",
            errors
        );
        var recordingsDirectory =
            NormalizeOptionalPath(settings.RecordingsDirectory, "Recordings directory", errors)
            ?? GetDefaultRecordingsDirectory();

        ValidateRecordingsDirectory(recordingsDirectory, errors);

        return errors.Count == 0
            ? new SettingsValidationResult(
                settings with
                {
                    WowLogsDirectory = wowLogsDirectory,
                    RecordingsDirectory = recordingsDirectory,
                },
                errors
            )
            : new SettingsValidationResult(null, errors);
    }

    private static string GetDefaultRecordingsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "PullWatch"
        );
    }

    private static string? NormalizeOptionalPath(
        string? path,
        string description,
        ICollection<string> errors
    )
    {
        if (path is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{description} cannot be empty.");
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{description} is invalid: {exception.Message}");
            return null;
        }
    }

    private static void ValidateRecordingsDirectory(string path, ICollection<string> errors)
    {
        var probePath = Path.Combine(path, $".pullwatch-write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(path);
            using (File.Create(probePath)) { }

            File.Delete(probePath);
        }
        catch (Exception exception)
        {
            errors.Add($"Recordings directory is not writable: {path}. {exception.Message}");

            try
            {
                File.Delete(probePath);
            }
            catch
            {
                // Preserve the original validation error.
            }
        }
    }
}
