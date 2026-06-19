using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed record SettingsBootstrapResult(PullWatchSettings Settings, bool CreatedSettingsFile);

public sealed class SettingsBootstrapper
{
    private readonly SettingsStore _store;
    private readonly ILogger<SettingsBootstrapper> _logger;
    private readonly Func<string?> _detectWowLogsDirectory;

    public SettingsBootstrapper(SettingsStore store, ILogger<SettingsBootstrapper> logger)
        : this(store, logger, WowLogsDirectoryDetector.Detect) { }

    internal SettingsBootstrapper(
        SettingsStore store,
        ILogger<SettingsBootstrapper> logger,
        Func<string?> detectWowLogsDirectory
    )
    {
        _store = store;
        _logger = logger;
        _detectWowLogsDirectory = detectWowLogsDirectory;
    }

    internal SettingsStore Store => _store;

    public async Task<PullWatchSettings?> LoadEffectiveAsync(CancellationToken cancellationToken)
    {
        return (await LoadEffectiveWithMetadataAsync(cancellationToken))?.Settings;
    }

    public async Task<SettingsBootstrapResult?> LoadEffectiveWithMetadataAsync(
        CancellationToken cancellationToken
    )
    {
        var loadResult = await _store.LoadAsync(cancellationToken);
        var shouldCreateSettingsFile = loadResult.Status == SettingsLoadStatus.Missing;
        var createdSettingsFile = false;
        PullWatchSettings settings;

        if (loadResult.Status == SettingsLoadStatus.Loaded)
        {
            var persistedValidation = SettingsValidator.Validate(loadResult.Settings!);

            if (persistedValidation.Settings is null)
            {
                _logger.LogError(
                    "Rejecting invalid settings file {SettingsPath}: {ValidationErrors}",
                    _store.SettingsPath,
                    string.Join(" ", persistedValidation.Errors)
                );
                settings = new PullWatchSettings();
            }
            else
            {
                settings = persistedValidation.Settings;
            }
        }
        else
        {
            if (loadResult.Status == SettingsLoadStatus.Invalid)
            {
                _logger.LogError(
                    loadResult.Error,
                    "Rejecting unreadable settings file {SettingsPath}; using defaults",
                    _store.SettingsPath
                );
            }
            else
            {
                _logger.LogInformation(
                    "Settings file does not exist at {SettingsPath}; creating it with defaults",
                    _store.SettingsPath
                );
            }

            settings = new PullWatchSettings();
        }

        if (settings.WowLogsDirectory is null)
        {
            settings = settings with { WowLogsDirectory = _detectWowLogsDirectory() };
        }

        var baseValidation = SettingsValidator.Validate(settings);

        if (baseValidation.Settings is null)
        {
            _logger.LogError(
                "Base settings are invalid: {ValidationErrors}",
                string.Join(" ", baseValidation.Errors)
            );
            return null;
        }

        settings = baseValidation.Settings;

        if (shouldCreateSettingsFile)
        {
            try
            {
                await _store.SaveAsync(settings, cancellationToken);
                createdSettingsFile = true;
                _logger.LogInformation(
                    "Created settings file with defaults at {SettingsPath}",
                    _store.SettingsPath
                );
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    exception,
                    "Could not create settings file at {SettingsPath}; continuing with in-memory defaults",
                    _store.SettingsPath
                );
            }
        }

        return new SettingsBootstrapResult(settings, createdSettingsFile);
    }
}
