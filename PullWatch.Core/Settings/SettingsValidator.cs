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

        if (!Enum.IsDefined(settings.Ui.SelectedRecordingCategory))
        {
            errors.Add(
                "Selected recording category must be ChallengeMode, RaidEncounter, or Manual."
            );
        }

        var wowLogsDirectory = NormalizeOptionalPath(
            settings.WowLogsDirectory,
            "WoW logs directory",
            errors
        );
        var recordingsDirectory =
            NormalizeOptionalPath(settings.RecordingsDirectory, "Recordings directory", errors)
            ?? GetDefaultRecordingsDirectory();

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

    public static IReadOnlyList<string> ValidateRecordingsDirectoryWritable(
        PullWatchSettings settings
    )
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        var recordingsDirectory =
            NormalizeOptionalPath(settings.RecordingsDirectory, "Recordings directory", errors)
            ?? GetDefaultRecordingsDirectory();

        if (errors.Count == 0)
        {
            ProbeRecordingsDirectory(recordingsDirectory, errors);
        }

        return errors;
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

    private static void ProbeRecordingsDirectory(string path, ICollection<string> errors)
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
