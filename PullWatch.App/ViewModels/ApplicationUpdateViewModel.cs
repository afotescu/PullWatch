using System.Windows.Media;

namespace PullWatch;

public sealed partial class ApplicationUpdateViewModel : ObservableObject
{
    private readonly IApplicationUpdater _updater;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<bool> _canRestartForUpdate;
    private readonly Action _requestShutdownForUpdate;

    private ApplicationUpdateState _state;
    private IApplicationUpdate? _availableUpdate;
    private IApplicationUpdate? _pendingUpdate;
    private int? _downloadProgress;
    private string? _lastMessage;

    internal ApplicationUpdateViewModel(
        IApplicationUpdater updater,
        IUiDispatcher dispatcher,
        Func<bool> canRestartForUpdate,
        Action requestShutdownForUpdate
    )
    {
        _updater = updater;
        _dispatcher = dispatcher;
        _canRestartForUpdate = canRestartForUpdate;
        _requestShutdownForUpdate = requestShutdownForUpdate;
        _pendingUpdate = updater.PendingUpdate;
        _state =
            _pendingUpdate is not null ? ApplicationUpdateState.ReadyToRestart
            : !_updater.CanCheckForUpdates ? ApplicationUpdateState.Unavailable
            : ApplicationUpdateState.ReadyToCheck;
    }

    public string ActionText =>
        _state switch
        {
            ApplicationUpdateState.Checking => "Checking...",
            ApplicationUpdateState.UpToDate => "Up to date",
            ApplicationUpdateState.UpdateAvailable => "Download update",
            ApplicationUpdateState.Downloading => _downloadProgress is { } progress
                ? $"Downloading {progress}%"
                : "Downloading...",
            ApplicationUpdateState.ReadyToRestart => "Restart to update",
            ApplicationUpdateState.Restarting => "Restarting...",
            _ => "Check for updates",
        };

    public string ActionToolTip =>
        _state switch
        {
            ApplicationUpdateState.ReadyToCheck => _lastMessage
                ?? "Check GitHub Releases for a PullWatch update.",
            ApplicationUpdateState.Checking => "Checking GitHub Releases for a PullWatch update.",
            ApplicationUpdateState.UpToDate => "PullWatch is up to date. Click to check again.",
            ApplicationUpdateState.UpdateAvailable => FormatUpdateAvailableToolTip(),
            ApplicationUpdateState.Downloading => FormatDownloadingToolTip(),
            ApplicationUpdateState.ReadyToRestart => FormatReadyToRestartToolTip(),
            ApplicationUpdateState.Restarting =>
                "PullWatch is closing so the update can be installed.",
            ApplicationUpdateState.Unavailable =>
                "Update checks are available only from installed PullWatch releases.",
            ApplicationUpdateState.Failed => _lastMessage ?? "Could not check for updates.",
            _ => "Check GitHub Releases for a PullWatch update.",
        };

    public Geometry ActionIcon => ShellIconGeometries.Update;

    public bool IsActionProminent =>
        _state
            is ApplicationUpdateState.UpdateAvailable
                or ApplicationUpdateState.Downloading
                or ApplicationUpdateState.ReadyToRestart
                or ApplicationUpdateState.Restarting;

    public void StartAutomaticCheck()
    {
        if (_state != ApplicationUpdateState.ReadyToCheck || !_updater.CanCheckForUpdates)
        {
            return;
        }

        _ = CheckForUpdatesAsync(isAutomatic: true);
    }

    public void RefreshCanRestart()
    {
        UpdateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ActionToolTip));
    }

    private bool CanRunUpdateAction()
    {
        return _state switch
        {
            ApplicationUpdateState.ReadyToCheck => true,
            ApplicationUpdateState.UpToDate => true,
            ApplicationUpdateState.UpdateAvailable => true,
            ApplicationUpdateState.ReadyToRestart => _canRestartForUpdate(),
            ApplicationUpdateState.Failed => true,
            _ => false,
        };
    }

    [RelayCommand(CanExecute = nameof(CanRunUpdateAction))]
    private async Task UpdateAsync()
    {
        switch (_state)
        {
            case ApplicationUpdateState.ReadyToCheck:
            case ApplicationUpdateState.UpToDate:
            case ApplicationUpdateState.Failed:
                await CheckForUpdatesAsync(isAutomatic: false);
                break;
            case ApplicationUpdateState.UpdateAvailable:
                await DownloadUpdateAsync();
                break;
            case ApplicationUpdateState.ReadyToRestart:
                RestartToUpdate();
                break;
        }
    }

    private async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        SetState(ApplicationUpdateState.Checking);

        try
        {
            var update = await _updater.CheckForUpdatesAsync(CancellationToken.None);

            if (update is null)
            {
                _lastMessage = null;
                SetState(
                    isAutomatic
                        ? ApplicationUpdateState.ReadyToCheck
                        : ApplicationUpdateState.UpToDate
                );
                return;
            }

            _availableUpdate = update;
            _pendingUpdate = null;
            _lastMessage = null;
            SetState(ApplicationUpdateState.UpdateAvailable);
        }
        catch (ApplicationUpdaterUnavailableException exception)
        {
            _lastMessage = exception.Message;
            SetState(ApplicationUpdateState.Unavailable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastMessage = $"Could not check for updates. {exception.Message}";
            SetState(
                isAutomatic ? ApplicationUpdateState.ReadyToCheck : ApplicationUpdateState.Failed
            );
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_availableUpdate is null)
        {
            SetState(ApplicationUpdateState.ReadyToCheck);
            return;
        }

        var update = _availableUpdate;
        _downloadProgress = null;
        _lastMessage = null;
        SetState(ApplicationUpdateState.Downloading);

        var progress = new DispatcherProgress(
            _dispatcher,
            value => SetDownloadProgress(Math.Clamp(value, 0, 100))
        );

        try
        {
            await _updater.DownloadUpdateAsync(update, progress, CancellationToken.None);
            _availableUpdate = null;
            _pendingUpdate = _updater.PendingUpdate ?? update;
            _downloadProgress = null;
            SetState(ApplicationUpdateState.ReadyToRestart);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastMessage = $"Could not download the update. {exception.Message}";
            _downloadProgress = null;
            SetState(ApplicationUpdateState.UpdateAvailable);
        }
    }

    private void RestartToUpdate()
    {
        if (_pendingUpdate is null)
        {
            SetState(ApplicationUpdateState.ReadyToCheck);
            return;
        }

        if (!_canRestartForUpdate())
        {
            RefreshCanRestart();
            return;
        }

        SetState(ApplicationUpdateState.Restarting);

        try
        {
            _updater.WaitForExitThenApplyUpdateAndRestart(_pendingUpdate);
            _requestShutdownForUpdate();
        }
        catch (Exception exception)
        {
            _lastMessage = $"Could not start the update. {exception.Message}";
            SetState(ApplicationUpdateState.ReadyToRestart);
        }
    }

    private string FormatUpdateAvailableToolTip()
    {
        if (_availableUpdate is null)
        {
            return WithLastMessage("A PullWatch update is available.");
        }

        return WithLastMessage(
            $"PullWatch {_availableUpdate.Version} is available. "
                + $"Download {FormatBytes(_availableUpdate.SizeBytes)} now and install it the next time PullWatch starts."
        );
    }

    private string FormatDownloadingToolTip()
    {
        if (_availableUpdate is null)
        {
            return "Downloading the PullWatch update.";
        }

        return _downloadProgress is { } progress
            ? $"Downloading PullWatch {_availableUpdate.Version}: {progress}%."
            : $"Downloading PullWatch {_availableUpdate.Version}.";
    }

    private string FormatReadyToRestartToolTip()
    {
        if (!_canRestartForUpdate())
        {
            return WithLastMessage(
                "Finish the active recording before restarting to update PullWatch."
            );
        }

        var message = _pendingUpdate is null
            ? "Restart PullWatch to install the update."
            : $"Restart PullWatch to install version {_pendingUpdate.Version}.";

        return WithLastMessage(message);
    }

    private string WithLastMessage(string message)
    {
        return _lastMessage is null ? message : $"{_lastMessage} {message}";
    }

    private void SetDownloadProgress(int progress)
    {
        if (_downloadProgress == progress)
        {
            return;
        }

        _downloadProgress = progress;
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ActionToolTip));
    }

    private void SetState(ApplicationUpdateState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        NotifyActionChanged();
    }

    private void NotifyActionChanged()
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ActionToolTip));
        OnPropertyChanged(nameof(ActionIcon));
        OnPropertyChanged(nameof(IsActionProminent));
        UpdateCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "the update";
        }

        const double kibibyte = 1024;
        const double mebibyte = kibibyte * 1024;
        const double gibibyte = mebibyte * 1024;

        return bytes >= gibibyte ? $"{bytes / gibibyte:0.#} GB" : $"{bytes / mebibyte:0.#} MB";
    }

    private enum ApplicationUpdateState
    {
        ReadyToCheck,
        Checking,
        UpToDate,
        UpdateAvailable,
        Downloading,
        ReadyToRestart,
        Restarting,
        Unavailable,
        Failed,
    }

    private sealed class DispatcherProgress(IUiDispatcher dispatcher, Action<int> report)
        : IProgress<int>
    {
        public void Report(int value)
        {
            dispatcher.Post(() => report(value));
        }
    }
}
