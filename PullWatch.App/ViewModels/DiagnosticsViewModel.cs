namespace PullWatch;

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly InMemoryLogProvider _logs;
    private readonly IDiagnosticsDialogs _dialogs;
    private ApplicationStatus _status;
    private string? _actionMessage;

    public DiagnosticsViewModel(
        ApplicationStatus initialStatus,
        InMemoryLogProvider logs,
        IDiagnosticsDialogs dialogs)
    {
        _status = initialStatus;
        _logs = logs;
        _dialogs = dialogs;
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        ExportDiagnosticsCommand = new AsyncRelayCommand(
            ExportDiagnosticsAsync,
            onException: HandleCommandFailure);
    }

    public RelayCommand CopyDiagnosticsCommand { get; }

    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

    public string CombatLogState => _status.CombatLog.State.ToString();

    public string CombatLogPath => Value(_status.CombatLog.CurrentPath);

    public string LastSuccessfulRead => Value(_status.CombatLog.LastSuccessfulReadTime);

    public string LastFileSystemError => Value(_status.CombatLog.LastFileSystemError);

    public string WowProcessState => _status.WowProcess.State.ToString();

    public string WowProcessId => Value(_status.WowProcess.ProcessId);

    public string WowWindowTitle => Value(_status.WowProcess.MainWindowTitle);

    public string WowProcessError => Value(_status.WowProcess.LastError);

    public string RecordingState => _status.Recording.State.ToString();

    public string RecordingOwner => Value(_status.Recording.Owner);

    public string ActiveOutputPath => Value(_status.Recording.ActiveOutputPath);

    public string LastRecorderFailure => Value(_status.Recording.LastFailure);

    public string RecentLogs => FormatLogs(_logs.GetSnapshot());

    public string? ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public void ApplyStatus(ApplicationStatus status)
    {
        _status = status;
        OnAllPropertiesChanged();
    }

    public void RefreshLogs()
    {
        OnPropertyChanged(nameof(RecentLogs));
    }

    private void CopyDiagnostics()
    {
        try
        {
            _dialogs.CopyText(BuildReport());
            ActionMessage = "Diagnostics copied to the clipboard.";
        }
        catch (Exception exception)
        {
            ActionMessage = $"Could not copy diagnostics: {exception.Message}";
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            var suggestedName = $"PullWatch-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var path = await _dialogs.PickDiagnosticsExportPathAsync(suggestedName);

            if (path is null)
            {
                return;
            }

            await _dialogs.WriteTextAsync(path, BuildReport(), CancellationToken.None);
            ActionMessage = $"Diagnostics exported to {path}";
        }
        catch (Exception exception)
        {
            ActionMessage = $"Could not export diagnostics: {exception.Message}";
        }
    }

    private string BuildReport()
    {
        return DiagnosticsReportBuilder.Build(
            ApplicationVersion.Current,
            _status,
            _logs.GetSnapshot());
    }

    private void HandleCommandFailure(Exception exception)
    {
        ActionMessage = $"Diagnostics command failed: {exception.Message}";
    }

    private static string FormatLogs(IReadOnlyList<ApplicationLogEntry> logs)
    {
        return logs.Count == 0
            ? "No application logs have been captured yet."
            : string.Join(
                Environment.NewLine,
                logs.Select(log =>
                    $"{log.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{log.Level}] {log.Category}: {log.Message}"));
    }

    private static string Value(object? value)
    {
        return value?.ToString() ?? "(none)";
    }
}
