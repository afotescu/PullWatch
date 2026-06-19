using System.Text;
using System.Text.Json;

namespace PullWatch;

public static class DiagnosticsReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Build(
        string appVersion,
        ApplicationStatus status,
        IReadOnlyList<ApplicationLogEntry> logs
    )
    {
        var report = new StringBuilder();
        report.AppendLine("PullWatch Diagnostics");
        report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        report.AppendLine($"App version: {appVersion}");
        report.AppendLine();
        report.AppendLine("Combat Log");
        report.AppendLine($"State: {status.CombatLog.State}");
        report.AppendLine(
            $"Active path: {DiagnosticsValueFormatter.Format(status.CombatLog.CurrentPath)}"
        );
        report.AppendLine(
            $"Last successful read: {DiagnosticsValueFormatter.Format(status.CombatLog.LastSuccessfulReadTime)}"
        );
        report.AppendLine(
            $"Last filesystem error: {DiagnosticsValueFormatter.Format(status.CombatLog.LastFileSystemError)}"
        );
        report.AppendLine();
        report.AppendLine("World of Warcraft");
        report.AppendLine($"State: {status.WowProcess.State}");
        report.AppendLine(
            $"Process id: {DiagnosticsValueFormatter.Format(status.WowProcess.ProcessId)}"
        );
        report.AppendLine(
            $"Window title: {DiagnosticsValueFormatter.Format(status.WowProcess.MainWindowTitle)}"
        );
        report.AppendLine(
            $"Last process error: {DiagnosticsValueFormatter.Format(status.WowProcess.LastError)}"
        );
        report.AppendLine();
        report.AppendLine("Recorder");
        report.AppendLine($"State: {status.Recording.State}");
        report.AppendLine($"Owner: {DiagnosticsValueFormatter.Format(status.Recording.Owner)}");
        report.AppendLine(
            $"Active output path: {DiagnosticsValueFormatter.Format(status.Recording.ActiveOutputPath)}"
        );
        report.AppendLine(
            $"Last failure: {DiagnosticsValueFormatter.Format(status.Recording.LastFailure)}"
        );
        report.AppendLine();
        report.AppendLine("Effective Settings");
        report.AppendLine(
            status.EffectiveSettings is null
                ? "(not loaded)"
                : JsonSerializer.Serialize(status.EffectiveSettings, JsonOptions)
        );
        report.AppendLine();
        report.AppendLine("Recent Application Logs");

        foreach (var log in logs)
        {
            report.AppendLine($"{log.Timestamp:O} [{log.Level}] {log.Category}: {log.Message}");

            if (log.Exception is not null)
            {
                report.AppendLine(log.Exception);
            }
        }

        return report.ToString();
    }
}
