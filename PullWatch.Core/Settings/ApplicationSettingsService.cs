namespace PullWatch;

public enum SettingsSaveStatus
{
    Saved,
    Invalid,
    RecordingActive,
    PersistenceFailed,
    ApplicationFailed
}

public sealed record SettingsSaveResult(
    SettingsSaveStatus Status,
    PullWatchSettings? Settings,
    IReadOnlyList<string> ValidationErrors,
    Exception? Error = null)
{
    public bool IsSaved =>
        Status is SettingsSaveStatus.Saved or SettingsSaveStatus.ApplicationFailed;
}

public sealed class ApplicationSettingsService(
    SettingsStore store,
    SettingsProvider provider)
{
    public PullWatchSettings Current => provider.Current;

    public async Task<SettingsSaveResult> SaveAsync(
        PullWatchSettings settings,
        CancellationToken cancellationToken)
    {
        var validation = SettingsValidator.Validate(settings);

        if (validation.Settings is null)
        {
            return new SettingsSaveResult(
                SettingsSaveStatus.Invalid,
                null,
                validation.Errors);
        }

        try
        {
            await store.SaveAsync(validation.Settings, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new SettingsSaveResult(
                SettingsSaveStatus.PersistenceFailed,
                null,
                [],
                exception);
        }

        provider.TryUpdate(validation.Settings);
        return new SettingsSaveResult(SettingsSaveStatus.Saved, validation.Settings, []);
    }
}
