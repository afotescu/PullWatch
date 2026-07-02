using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class StartupUpdateInstallerTests
{
    [Fact]
    public void NoPendingUpdateDoesNotRequestShutdown()
    {
        var updater = new FakeApplicationUpdater();
        var shutdownRequested = false;

        var applied = StartupUpdateInstaller.TryApplyPendingUpdateAndRestart(
            updater,
            () => shutdownRequested = true,
            NullLogger.Instance
        );

        Assert.False(applied);
        Assert.False(shutdownRequested);
        Assert.Empty(updater.AppliedUpdates);
    }

    [Fact]
    public void PendingUpdateSchedulesApplyAndRequestsShutdown()
    {
        var update = new FakeApplicationUpdate("1.2.3", 12 * 1024 * 1024);
        var updater = new FakeApplicationUpdater { PendingUpdate = update };
        var shutdownRequested = false;

        var applied = StartupUpdateInstaller.TryApplyPendingUpdateAndRestart(
            updater,
            () => shutdownRequested = true,
            NullLogger.Instance
        );

        Assert.True(applied);
        Assert.True(shutdownRequested);
        Assert.Equal([update], updater.AppliedUpdates);
    }

    [Fact]
    public void ApplyFailureKeepsStartupRunning()
    {
        var updater = new FakeApplicationUpdater
        {
            PendingUpdate = new FakeApplicationUpdate("1.2.3", 12 * 1024 * 1024),
            ApplyException = new ApplicationUpdateException("Could not start updater."),
        };
        var shutdownRequested = false;

        var applied = StartupUpdateInstaller.TryApplyPendingUpdateAndRestart(
            updater,
            () => shutdownRequested = true,
            NullLogger.Instance
        );

        Assert.False(applied);
        Assert.False(shutdownRequested);
    }

    private sealed class FakeApplicationUpdater : IApplicationUpdater
    {
        public bool CanCheckForUpdates => true;
        public IApplicationUpdate? PendingUpdate { get; init; }
        public Exception? ApplyException { get; init; }
        public List<IApplicationUpdate> AppliedUpdates { get; } = [];

        public Task<IApplicationUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IApplicationUpdate?>(null);
        }

        public Task DownloadUpdateAsync(
            IApplicationUpdate update,
            IProgress<int> progress,
            CancellationToken cancellationToken
        )
        {
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
}
