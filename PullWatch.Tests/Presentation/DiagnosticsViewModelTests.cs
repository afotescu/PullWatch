namespace PullWatch.Tests;

public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public async Task ExportsTimestampedPlainTextDiagnostics()
    {
        using var logs = new InMemoryLogProvider();
        var dialogs = new FakeDiagnosticsDialogs();
        var viewModel = new DiagnosticsViewModel(Status(), logs, dialogs);

        await viewModel.ExportDiagnosticsCommand.ExecuteAsync();

        Assert.Matches(@"PullWatch-diagnostics-\d{8}-\d{6}\.txt", dialogs.SuggestedFileName!);
        Assert.Equal(@"C:\Temp\diagnostics.txt", dialogs.WrittenPath);
        Assert.Contains("PullWatch Diagnostics", dialogs.WrittenText);
        Assert.Contains("Effective Settings", dialogs.WrittenText);
    }

    private static ApplicationStatus Status()
    {
        return new ApplicationStatus(
            new PullWatchSettings(),
            new RecordingCoordinatorStatus(
                RecordingCoordinatorState.Idle,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            new CombatLogReaderStatus(
                CombatLogReaderState.WaitingForCombatLog,
                null,
                null,
                null));
    }

    private sealed class FakeDiagnosticsDialogs : IDiagnosticsDialogs
    {
        public string? SuggestedFileName { get; private set; }

        public string? WrittenPath { get; private set; }

        public string? WrittenText { get; private set; }

        public void CopyText(string text)
        {
        }

        public Task<string?> PickDiagnosticsExportPathAsync(string suggestedFileName)
        {
            SuggestedFileName = suggestedFileName;
            return Task.FromResult<string?>(@"C:\Temp\diagnostics.txt");
        }

        public Task WriteTextAsync(string path, string text, CancellationToken cancellationToken)
        {
            WrittenPath = path;
            WrittenText = text;
            return Task.CompletedTask;
        }
    }
}
