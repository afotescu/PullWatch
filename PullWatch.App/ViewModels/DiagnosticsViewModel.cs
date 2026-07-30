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
                GetCombatLogIndicator(_status.CombatLog),
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
                GetWowProcessIndicator(_status.WowProcess),
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
                GetRecordingIndicator(_status.Recording),
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

    private static DiagnosticIndicatorKind GetCombatLogIndicator(CombatLogReaderStatus status)
    {
        if (status.LastFileSystemError is not null)
        {
            return DiagnosticIndicatorKind.Error;
        }

        return status.State switch
        {
            CombatLogReaderState.ReadingCombatLog => DiagnosticIndicatorKind.Success,
            CombatLogReaderState.SwitchingCombatLog => DiagnosticIndicatorKind.Warning,
            _ => DiagnosticIndicatorKind.Idle,
        };
    }

    private static DiagnosticIndicatorKind GetWowProcessIndicator(WowProcessStatus status)
    {
        if (status.LastError is not null)
        {
            return DiagnosticIndicatorKind.Error;
        }

        return status.State == WowProcessState.WindowAvailable
            ? DiagnosticIndicatorKind.Success
            : DiagnosticIndicatorKind.Idle;
    }

    private static DiagnosticIndicatorKind GetRecordingIndicator(RecordingCoordinatorStatus status)
    {
        if (status.LastFailure is not null)
        {
            return DiagnosticIndicatorKind.Error;
        }

        return status.State switch
        {
            RecordingCoordinatorState.Recording => DiagnosticIndicatorKind.Recording,
            RecordingCoordinatorState.Starting or RecordingCoordinatorState.Stopping =>
                DiagnosticIndicatorKind.Warning,
            _ => DiagnosticIndicatorKind.Idle,
        };
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

public enum DiagnosticIndicatorKind
{
    Idle,
    Success,
    Warning,
    Recording,
    Error,
}

public sealed record DiagnosticsSectionViewModel(
    string Title,
    DiagnosticIndicatorKind Indicator,
    IReadOnlyList<DiagnosticRowViewModel> Rows
)
{
    public string State => Rows[0].Value;

    public IEnumerable<DiagnosticRowViewModel> Details => Rows.Skip(1);
}

public sealed record DiagnosticRowViewModel(string Label, string Value);
