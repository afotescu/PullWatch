using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace PullWatch.Tests;

public sealed class RecordingStorageCoordinatorTests
{
    [Fact]
    public async Task ActiveCleanupKeepsStartingPolicyAndQueuedOperationsCoalesceToLatest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cleanupStarted = CreateCompletionSource();
        var allowCleanupToFinish = CreateCompletionSource();
        var latestRefreshCompleted = new TaskCompletionSource<RecordingStorageStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var enforcedDirectories = new ConcurrentQueue<string?>();
        var refreshedDirectories = new ConcurrentQueue<string?>();
        await using var coordinator = new RecordingStorageCoordinator(
            getUsage: (settings, _) =>
            {
                refreshedDirectories.Enqueue(settings.RecordingsDirectory);
                var usageBytes = settings.RecordingsDirectory == "latest" ? 300 : 200;
                return Task.FromResult(new RecordingStorageUsage(usageBytes, 3));
            },
            enforceLimit: async (settings, _) =>
            {
                enforcedDirectories.Enqueue(settings.RecordingsDirectory);
                cleanupStarted.TrySetResult();
                await allowCleanupToFinish.Task;
                return new RecordingStorageCleanupResult(new RecordingStorageUsage(45, 1), 1, []);
            },
            NullLogger<RecordingStorageCoordinator>.Instance
        );
        coordinator.StatusChanged += status =>
        {
            if (status.UsageBytes == 300 && !status.IsRefreshing)
            {
                latestRefreshCompleted.TrySetResult(status);
            }
        };

        coordinator.QueueRefreshOrRetention(Settings("active", maxUsageBytes: 50));
        await cleanupStarted.Task.WaitAsync(cancellationToken);

        coordinator.QueueRefreshOrRetention(
            Settings("superseded", RecordingStorageSettings.UnlimitedBytes)
        );
        coordinator.QueueRefreshOrRetention(
            Settings("latest", RecordingStorageSettings.UnlimitedBytes)
        );
        allowCleanupToFinish.TrySetResult();

        var finalStatus = await latestRefreshCompleted.Task.WaitAsync(cancellationToken);

        Assert.Equal(["active"], enforcedDirectories);
        Assert.Equal(["latest"], refreshedDirectories);
        Assert.Equal(300, finalStatus.UsageBytes);
        Assert.Equal(3, finalStatus.RecordingCount);
    }

    [Fact]
    public async Task DisposalCancelsAndWaitsForActiveOperation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var operationStarted = CreateCompletionSource();
        var cancellationObserved = CreateCompletionSource();
        var allowOperationToFinish = CreateCompletionSource();
        var coordinator = new RecordingStorageCoordinator(
            getUsage: (_, _) => Task.FromResult(new RecordingStorageUsage(0, 0)),
            enforceLimit: async (_, operationCancellationToken) =>
            {
                operationStarted.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, operationCancellationToken);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                    await allowOperationToFinish.Task;
                }

                return new RecordingStorageCleanupResult(new RecordingStorageUsage(0, 0), 0, []);
            },
            NullLogger<RecordingStorageCoordinator>.Instance
        );
        coordinator.QueueRefreshOrRetention(Settings("recordings", maxUsageBytes: 50));
        await operationStarted.Task.WaitAsync(cancellationToken);

        var disposal = coordinator.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(cancellationToken);

        Assert.False(disposal.IsCompleted);

        allowOperationToFinish.TrySetResult();
        await disposal.WaitAsync(cancellationToken);
    }

    private static PullWatchSettings Settings(string recordingsDirectory, long maxUsageBytes)
    {
        return new PullWatchSettings
        {
            RecordingsDirectory = recordingsDirectory,
            Storage = new RecordingStorageSettings { MaxUsageBytes = maxUsageBytes },
        };
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
