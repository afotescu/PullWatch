namespace PullWatch.Tests;

public sealed class ApplicationUpdateViewModelTests
{
    [Fact]
    public void UnavailableUpdaterShowsDisabledCheckAction()
    {
        var updater = new FakeApplicationUpdater { CanCheckForUpdates = false };
        var viewModel = CreateViewModel(updater);

        Assert.Equal("Check for updates", viewModel.ActionText);
        Assert.False(viewModel.IsActionProminent);
        Assert.False(viewModel.UpdateCommand.CanExecute(null));
        Assert.Contains(
            "installed PullWatch releases",
            viewModel.ActionToolTip,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ManualCheckWithoutUpdateReportsUpToDate()
    {
        var updater = new FakeApplicationUpdater();
        var viewModel = CreateViewModel(updater);

        await viewModel.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Up to date", viewModel.ActionText);
        Assert.False(viewModel.IsActionProminent);
        Assert.Contains("up to date", viewModel.ActionToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, updater.CheckCount);
    }

    [Fact]
    public async Task AutomaticCheckReportsAvailableUpdateWithoutDownloading()
    {
        var updater = new FakeApplicationUpdater
        {
            CheckResult = new FakeApplicationUpdate("1.2.3", 52 * 1024 * 1024),
        };
        var viewModel = CreateViewModel(updater);

        viewModel.StartAutomaticCheck();

        await WaitForAsync(() => viewModel.ActionText == "Download update");

        Assert.Equal(1, updater.CheckCount);
        Assert.True(viewModel.IsActionProminent);
        Assert.Empty(updater.DownloadedUpdates);
        Assert.Contains("1.2.3", viewModel.ActionToolTip, StringComparison.Ordinal);
        Assert.Contains("52 MB", viewModel.ActionToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AvailableUpdateDownloadsOnlyAfterUserAction()
    {
        var update = new FakeApplicationUpdate("1.2.3", 12 * 1024 * 1024);
        var updater = new FakeApplicationUpdater { CheckResult = update };
        var viewModel = CreateViewModel(updater);

        await viewModel.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Download update", viewModel.ActionText);
        Assert.True(viewModel.IsActionProminent);
        Assert.Empty(updater.DownloadedUpdates);

        await viewModel.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Restart to update", viewModel.ActionText);
        Assert.True(viewModel.IsActionProminent);
        Assert.Equal([update], updater.DownloadedUpdates);
        Assert.Contains("1.2.3", viewModel.ActionToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyUpdateStartsUpdaterAndRequestsShutdown()
    {
        var update = new FakeApplicationUpdate("1.2.3", 12 * 1024 * 1024);
        var shutdownRequested = false;
        var updater = new FakeApplicationUpdater { PendingUpdate = update };
        var viewModel = CreateViewModel(
            updater,
            requestShutdownForUpdate: () => shutdownRequested = true
        );

        await viewModel.UpdateCommand.ExecuteAsync(null);

        Assert.True(shutdownRequested);
        Assert.Equal([update], updater.AppliedUpdates);
        Assert.Equal("Restarting...", viewModel.ActionText);
        Assert.True(viewModel.IsActionProminent);
    }

    [Fact]
    public void ReadyUpdateCannotRestartWhileRecordingIsActive()
    {
        var updater = new FakeApplicationUpdater
        {
            PendingUpdate = new FakeApplicationUpdate("1.2.3", 12 * 1024 * 1024),
        };
        var viewModel = CreateViewModel(updater, canRestartForUpdate: () => false);

        Assert.False(viewModel.UpdateCommand.CanExecute(null));
        Assert.True(viewModel.IsActionProminent);
        Assert.Contains(
            "Finish the active recording",
            viewModel.ActionToolTip,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task DownloadFailureKeepsUpdateActionAvailable()
    {
        var updater = new FakeApplicationUpdater
        {
            CheckResult = new FakeApplicationUpdate("1.2.3", 12 * 1024 * 1024),
            DownloadException = new ApplicationUpdateException("Network unavailable."),
        };
        var viewModel = CreateViewModel(updater);

        await viewModel.UpdateCommand.ExecuteAsync(null);
        await viewModel.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Download update", viewModel.ActionText);
        Assert.True(viewModel.IsActionProminent);
        Assert.Contains("Could not download", viewModel.ActionToolTip, StringComparison.Ordinal);
        Assert.True(viewModel.UpdateCommand.CanExecute(null));
    }

    private static ApplicationUpdateViewModel CreateViewModel(
        FakeApplicationUpdater updater,
        Func<bool>? canRestartForUpdate = null,
        Action? requestShutdownForUpdate = null
    )
    {
        return new ApplicationUpdateViewModel(
            updater,
            ImmediateUiDispatcher.Instance,
            canRestartForUpdate ?? (() => true),
            requestShutdownForUpdate ?? (() => { })
        );
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeApplicationUpdater : IApplicationUpdater
    {
        public bool CanCheckForUpdates { get; init; } = true;
        public IApplicationUpdate? PendingUpdate { get; set; }
        public IApplicationUpdate? CheckResult { get; init; }
        public Exception? CheckException { get; init; }
        public Exception? DownloadException { get; init; }
        public Exception? ApplyException { get; init; }
        public int CheckCount { get; private set; }
        public List<IApplicationUpdate> DownloadedUpdates { get; } = [];
        public List<IApplicationUpdate> AppliedUpdates { get; } = [];

        public Task<IApplicationUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            CheckCount++;

            if (CheckException is not null)
            {
                throw CheckException;
            }

            return Task.FromResult(CheckResult);
        }

        public Task DownloadUpdateAsync(
            IApplicationUpdate update,
            IProgress<int> progress,
            CancellationToken cancellationToken
        )
        {
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            progress.Report(100);
            DownloadedUpdates.Add(update);
            PendingUpdate = update;
            return Task.CompletedTask;
        }

        public void WaitForExitThenApplyUpdateAndRestart(IApplicationUpdate update)
        {
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            AppliedUpdates.Add(update);
        }
    }

    private sealed record FakeApplicationUpdate(
        string Version,
        long SizeBytes,
        string? ReleaseNotesMarkdown = null
    ) : IApplicationUpdate;

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public void Post(Action action)
        {
            action();
        }
    }
}
