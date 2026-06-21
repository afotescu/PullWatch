namespace PullWatch.Tests;

public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public async Task ExportsTimestampedPlainTextDiagnostics()
    {
        using var logs = new InMemoryLogProvider();
        var dialogs = new FakeDiagnosticsDialogs();
        var viewModel = new DiagnosticsViewModel(Status(), logs, dialogs);

        await viewModel.ExportDiagnosticsCommand.ExecuteAsync(null);

        Assert.Matches(@"PullWatch-diagnostics-\d{8}-\d{6}\.txt", dialogs.SuggestedFileName!);
        Assert.Equal(@"C:\Temp\diagnostics.txt", dialogs.WrittenPath);
        Assert.Contains("PullWatch Diagnostics", dialogs.WrittenText);
        Assert.Contains($"App version: {ApplicationVersion.Current}", dialogs.WrittenText);
        Assert.Contains("Effective Settings", dialogs.WrittenText);
    }

    [Fact]
    public void ExposesDiagnosticsSections()
    {
        using var logs = new InMemoryLogProvider();
        var viewModel = new DiagnosticsViewModel(Status(), logs, new FakeDiagnosticsDialogs());

        Assert.Collection(
            viewModel.Sections,
            combatLog =>
            {
                Assert.Equal("Combat log reader", combatLog.Title);
                AssertRows(
                    combatLog.Rows,
                    ("State", "WaitingForCombatLog"),
                    ("Active path", "(none)"),
                    ("Last successful read", "(none)"),
                    ("Last filesystem error", "(none)")
                );
            },
            wowProcess =>
            {
                Assert.Equal("World of Warcraft", wowProcess.Title);
                AssertRows(
                    wowProcess.Rows,
                    ("State", "WaitingForProcess"),
                    ("Process id", "(none)"),
                    ("Window title", "(none)"),
                    ("Last process error", "(none)")
                );
            },
            recording =>
            {
                Assert.Equal("Recorder", recording.Title);
                AssertRows(
                    recording.Rows,
                    ("State", "Idle"),
                    ("Owner", "(none)"),
                    ("Active output path", "(none)"),
                    ("Last failure", "(none)")
                );
            }
        );
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
                null
            ),
            new CombatLogReaderStatus(CombatLogReaderState.WaitingForCombatLog, null, null, null),
            new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null)
        );
    }

    private static void AssertRows(
        IReadOnlyList<DiagnosticRowViewModel> rows,
        params (string Label, string Value)[] expected
    )
    {
        Assert.Equal(expected.Length, rows.Count);

        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Label, rows[index].Label);
            Assert.Equal(expected[index].Value, rows[index].Value);
        }
    }

    private sealed class FakeDiagnosticsDialogs : IDiagnosticsDialogs
    {
        public string? SuggestedFileName { get; private set; }

        public string? WrittenPath { get; private set; }

        public string? WrittenText { get; private set; }

        public void CopyText(string text) { }

        public Task<string?> PickDiagnosticsExportPathAsync(string suggestedFileName)
        {
            SuggestedFileName = suggestedFileName;
            return Task.FromResult<string?>(@"C:\Temp\diagnostics.txt");
        }

        public Task WriteTextAsync(string path, string text)
        {
            WrittenPath = path;
            WrittenText = text;
            return Task.CompletedTask;
        }
    }
}
