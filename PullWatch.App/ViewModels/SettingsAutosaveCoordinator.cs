namespace PullWatch;

[Flags]
internal enum SettingsSaveScope
{
    None = 0,
    WowLogsDirectory = 1,
    RecordingsDirectory = 2,
    StartupShortcut = 4,
}

internal sealed record SettingsAutosaveOutcome(
    SettingsSaveScope Scope,
    PullWatchSettings AttemptedSettings,
    SettingsSaveResult? SaveResult,
    Exception? Exception,
    bool ShouldSyncStartupShortcut,
    bool WasSkipped
);

internal sealed class SettingsAutosaveCoordinator
{
    private readonly Func<PullWatchSettings, SettingsSaveScope, PullWatchSettings> _serialize;
    private readonly Func<PullWatchSettings, Task<SettingsSaveResult>> _save;
    private readonly Func<SettingsAutosaveOutcome, Task> _handleResult;
    private readonly Func<bool> _canSave;
    private readonly object _sync = new();
    private PullWatchSettings _savedSettings;
    private Task? _autosaveTask;
    private SettingsSaveScope? _pendingSave;
    private SettingsSaveScope _retryOnNextSave;

    public SettingsAutosaveCoordinator(
        PullWatchSettings savedSettings,
        Func<bool> canSave,
        Func<PullWatchSettings, SettingsSaveScope, PullWatchSettings> serialize,
        Func<PullWatchSettings, Task<SettingsSaveResult>> save,
        Func<SettingsAutosaveOutcome, Task> handleResult
    )
    {
        _savedSettings = savedSettings;
        _canSave = canSave;
        _serialize = serialize;
        _save = save;
        _handleResult = handleResult;
    }

    public PullWatchSettings SavedSettings
    {
        get
        {
            lock (_sync)
            {
                return _savedSettings;
            }
        }
    }

    public bool HasPendingSave
    {
        get
        {
            lock (_sync)
            {
                return _pendingSave is not null || _autosaveTask is { IsCompleted: false };
            }
        }
    }

    public Task QueueSaveAsync(SettingsSaveScope requestedScope)
    {
        lock (_sync)
        {
            _pendingSave = (_pendingSave ?? SettingsSaveScope.None) | requestedScope;
            _autosaveTask ??= ProcessSavesAsync();

            if (_autosaveTask.IsCompleted)
            {
                _autosaveTask = ProcessSavesAsync();
            }

            return _autosaveTask;
        }
    }

    public void UpdateSavedSettings(PullWatchSettings settings)
    {
        lock (_sync)
        {
            _savedSettings = settings;
        }
    }

    public void SetRetryOnNextSave(SettingsSaveScope scope, bool shouldRetry)
    {
        lock (_sync)
        {
            _retryOnNextSave = shouldRetry ? _retryOnNextSave | scope : _retryOnNextSave & ~scope;
        }
    }

    private async Task ProcessSavesAsync()
    {
        await Task.Yield();

        while (true)
        {
            SettingsSaveScope scope;
            PullWatchSettings previousSettings;

            lock (_sync)
            {
                if (_pendingSave is null)
                {
                    _autosaveTask = null;
                    return;
                }

                scope = _pendingSave.Value | _retryOnNextSave;
                _pendingSave = null;
                previousSettings = _savedSettings;
            }

            if (!_canSave())
            {
                await _handleResult(
                    new SettingsAutosaveOutcome(
                        scope,
                        previousSettings,
                        null,
                        null,
                        ShouldSyncStartupShortcut: false,
                        WasSkipped: true
                    )
                );
                continue;
            }

            var attemptedSettings = _serialize(previousSettings, scope);
            var shouldSyncStartupShortcut =
                Includes(scope, SettingsSaveScope.StartupShortcut)
                || attemptedSettings.Startup != previousSettings.Startup;
            SettingsSaveResult? saveResult = null;
            Exception? exception = null;

            try
            {
                saveResult = await _save(attemptedSettings);

                if (saveResult.WasPersisted)
                {
                    lock (_sync)
                    {
                        _savedSettings = saveResult.Settings!;
                    }
                }
            }
            catch (Exception caught)
            {
                exception = caught;
            }

            await _handleResult(
                new SettingsAutosaveOutcome(
                    scope,
                    attemptedSettings,
                    saveResult,
                    exception,
                    shouldSyncStartupShortcut,
                    WasSkipped: false
                )
            );
        }
    }

    public static bool Includes(SettingsSaveScope value, SettingsSaveScope scope)
    {
        return (value & scope) != SettingsSaveScope.None;
    }
}
