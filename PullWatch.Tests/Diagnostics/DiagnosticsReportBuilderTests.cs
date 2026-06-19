using Microsoft.Extensions.Logging;

namespace PullWatch.Tests;

public sealed class DiagnosticsReportBuilderTests
{
    [Fact]
    public void IncludesVersionSettingsPathsFailuresAndLogs()
    {
        var failure = new InvalidOperationException("recorder failed");
        var status = new ApplicationStatus(
            new PullWatchSettings
            {
                WowLogsDirectory = @"C:\World of Warcraft\_retail_\Logs",
                RecordingsDirectory = @"D:\Recordings"
            },
            new RecordingCoordinatorStatus(
                RecordingCoordinatorState.Recording,
                RecordingOwner.Manual,
                null,
                new ManualRecordingContext(DateTimeOffset.Now),
                null,
                null,
                failure,
                @"D:\Recordings\active.mp4"),
            new CombatLogReaderStatus(
                CombatLogReaderState.ReadingCombatLog,
                @"C:\World of Warcraft\_retail_\Logs\WoWCombatLog.txt",
                DateTimeOffset.UtcNow,
                null),
            new WowProcessStatus(
                WowProcessState.WindowAvailable,
                1234,
                "World of Warcraft",
                null));

        var report = DiagnosticsReportBuilder.Build(
            "1.2.3",
            status,
            [new ApplicationLogEntry(
                DateTimeOffset.Now,
                LogLevel.Warning,
                "PullWatch.Test",
                new EventId(1),
                "test warning",
                null)]);

        Assert.Contains("App version: 1.2.3", report);
        Assert.Contains(status.CombatLog.CurrentPath!, report);
        Assert.Contains("WindowAvailable", report);
        Assert.Contains("1234", report);
        Assert.Contains(status.Recording.ActiveOutputPath!, report);
        Assert.Contains("recorder failed", report);
        Assert.Contains("\"RecordingsDirectory\": \"D:\\\\Recordings\"", report);
        Assert.Contains("test warning", report);
    }
}
