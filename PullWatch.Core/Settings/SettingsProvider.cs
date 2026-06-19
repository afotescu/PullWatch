namespace PullWatch;

public sealed class SettingsProvider
{
    private PullWatchSettings _current;

    public SettingsProvider(PullWatchSettings initialSettings)
    {
        _current = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
    }

    public PullWatchSettings Current => Volatile.Read(ref _current);

    public SettingsValidationResult TryUpdate(PullWatchSettings settings)
    {
        var result = SettingsValidator.Validate(settings);

        if (result.Settings is not null)
        {
            Volatile.Write(ref _current, result.Settings);
        }

        return result;
    }

    public void UpdateValidated(PullWatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Volatile.Write(ref _current, settings);
    }
}
