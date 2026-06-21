namespace PullWatch;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly InMemoryLogProvider _logs;
    private readonly IDiagnosticsDialogs _dialogs;
    private ApplicationStatus _status;
    private string? _actionMessage;

    public DiagnosticsViewModel(
        ApplicationStatus initialStatus,
        InMemoryLogProvider logs,
        IDiagnosticsDialogs dialogs
    )
    {
        _status = initialStatus;
        _logs = logs;
        _dialogs = dialogs;
    }

    public IReadOnlyList<DiagnosticsSectionViewModel> Sections =>
        [
            new(
                "Combat log reader",
                [
                    new("State", _status.CombatLog.State.ToString()),
                    new(
                        "Active path",
                        DiagnosticsValueFormatter.Format(_status.CombatLog.CurrentPath)
                    ),
                    new(
                        "Last successful read",
                        DiagnosticsValueFormatter.Format(_status.CombatLog.LastSuccessfulReadTime)
                    ),
                    new(
                        "Last filesystem error",
                        DiagnosticsValueFormatter.Format(_status.CombatLog.LastFileSystemError)
                    ),
                ]
            ),
            new(
                "World of Warcraft",
                [
                    new("State", _status.WowProcess.State.ToString()),
                    new(
                        "Process id",
                        DiagnosticsValueFormatter.Format(_status.WowProcess.ProcessId)
                    ),
                    new(
                        "Process started at",
                        DiagnosticsValueFormatter.Format(_status.WowProcess.ProcessStartedAtUtc)
                    ),
                    new(
                        "Window title",
                        DiagnosticsValueFormatter.Format(_status.WowProcess.MainWindowTitle)
                    ),
                    new(
                        "Last process error",
                        DiagnosticsValueFormatter.Format(_status.WowProcess.LastError)
                    ),
                ]
            ),
            new(
                "Recorder",
                [
                    new("State", _status.Recording.State.ToString()),
                    new("Owner", DiagnosticsValueFormatter.Format(_status.Recording.Owner)),
                    new(
                        "Active output path",
                        DiagnosticsValueFormatter.Format(_status.Recording.ActiveOutputPath)
                    ),
                    new(
                        "Last failure",
                        DiagnosticsValueFormatter.Format(_status.Recording.LastFailure)
                    ),
                ]
            ),
        ];

    public string RecentLogs => FormatLogs(_logs.GetSnapshot());

    public string? ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public void ApplyStatus(ApplicationStatus status)
    {
        _status = status;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshLogs()
    {
        OnPropertyChanged(nameof(RecentLogs));
    }

    [RelayCommand]
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

    [RelayCommand]
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

            await _dialogs.WriteTextAsync(path, BuildReport());
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
            _logs.GetSnapshot()
        );
    }

    private static string FormatLogs(IReadOnlyList<ApplicationLogEntry> logs)
    {
        return logs.Count == 0
            ? "No application logs have been captured yet."
            : string.Join(
                Environment.NewLine,
                logs.Select(log =>
                    $"{log.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{log.Level}] {log.Category}: {log.Message}"
                )
            );
    }
}

public sealed record DiagnosticsSectionViewModel(
    string Title,
    IReadOnlyList<DiagnosticRowViewModel> Rows
);

public sealed record DiagnosticRowViewModel(string Label, string Value);
