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

    [Fact]
    public async Task UsageRefreshWithEnabledLimitNeverEnforcesRetention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var refreshCompleted = new TaskCompletionSource<RecordingStorageStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var enforceCallCount = 0;
        await using var coordinator = new RecordingStorageCoordinator(
            getUsage: (_, _) => Task.FromResult(new RecordingStorageUsage(95, 3, 90, 2)),
            enforceLimit: (_, _) =>
            {
                Interlocked.Increment(ref enforceCallCount);
                return Task.FromResult(
                    new RecordingStorageCleanupResult(new RecordingStorageUsage(0, 0), 0, [])
                );
            },
            NullLogger<RecordingStorageCoordinator>.Instance
        );
        coordinator.StatusChanged += status =>
        {
            if (status.UsageBytes == 95 && !status.IsRefreshing)
            {
                refreshCompleted.TrySetResult(status);
            }
        };

        coordinator.QueueUsageRefresh(Settings("recordings", maxUsageBytes: 100));

        var status = await refreshCompleted.Task.WaitAsync(cancellationToken);
        Assert.Equal(0, enforceCallCount);
        Assert.Equal(90, status.FavoriteUsageBytes);
        Assert.Equal(2, status.FavoriteRecordingCount);
        Assert.True(status.IsFavoriteCapacityConstrained);
    }

    [Fact]
    public async Task UsageRefreshDoesNotSupersedeQueuedRetention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activeRefreshStarted = CreateCompletionSource();
        var allowActiveRefreshToFinish = CreateCompletionSource();
        var queuedRetentionCompleted = CreateCompletionSource();
        var refreshCallCount = 0;
        var enforceCallCount = 0;
        await using var coordinator = new RecordingStorageCoordinator(
            getUsage: async (_, _) =>
            {
                if (Interlocked.Increment(ref refreshCallCount) == 1)
                {
                    activeRefreshStarted.TrySetResult();
                    await allowActiveRefreshToFinish.Task;
                }

                return new RecordingStorageUsage(95, 3, 90, 2);
            },
            enforceLimit: (_, _) =>
            {
                Interlocked.Increment(ref enforceCallCount);
                queuedRetentionCompleted.TrySetResult();
                return Task.FromResult(
                    new RecordingStorageCleanupResult(
                        new RecordingStorageUsage(90, 2, 90, 2),
                        1,
                        []
                    )
                );
            },
            NullLogger<RecordingStorageCoordinator>.Instance
        );

        coordinator.QueueUsageRefresh(Settings("active-refresh", maxUsageBytes: 100));
        await activeRefreshStarted.Task.WaitAsync(cancellationToken);

        coordinator.QueueRefreshOrRetention(Settings("queued-cleanup", maxUsageBytes: 100));
        coordinator.QueueUsageRefresh(Settings("favorite-refresh", maxUsageBytes: 100));
        allowActiveRefreshToFinish.TrySetResult();

        await queuedRetentionCompleted.Task.WaitAsync(cancellationToken);
        Assert.Equal(1, enforceCallCount);
    }

    [Fact]
    public async Task PendingUsageRefreshesCoalesceToLatestRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activeRefreshStarted = CreateCompletionSource();
        var allowActiveRefreshToFinish = CreateCompletionSource();
        var latestRefreshCompleted = CreateCompletionSource();
        var refreshedDirectories = new ConcurrentQueue<string?>();
        await using var coordinator = new RecordingStorageCoordinator(
            getUsage: async (settings, _) =>
            {
                refreshedDirectories.Enqueue(settings.RecordingsDirectory);

                if (settings.RecordingsDirectory == "active")
                {
                    activeRefreshStarted.TrySetResult();
                    await allowActiveRefreshToFinish.Task;
                }

                if (settings.RecordingsDirectory == "latest")
                {
                    latestRefreshCompleted.TrySetResult();
                }

                return new RecordingStorageUsage(50, 1, 25, 1);
            },
            enforceLimit: (_, _) =>
                Task.FromResult(
                    new RecordingStorageCleanupResult(new RecordingStorageUsage(0, 0), 0, [])
                ),
            NullLogger<RecordingStorageCoordinator>.Instance
        );

        coordinator.QueueUsageRefresh(Settings("active", maxUsageBytes: 100));
        await activeRefreshStarted.Task.WaitAsync(cancellationToken);

        coordinator.QueueUsageRefresh(Settings("superseded", maxUsageBytes: 100));
        coordinator.QueueUsageRefresh(Settings("latest", maxUsageBytes: 100));
        allowActiveRefreshToFinish.TrySetResult();

        await latestRefreshCompleted.Task.WaitAsync(cancellationToken);
        Assert.Equal(["active", "latest"], refreshedDirectories);
    }

    [Fact]
    public async Task ExclusiveMutationWaitsForActiveRetention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cleanupStarted = CreateCompletionSource();
        var allowCleanupToFinish = CreateCompletionSource();
        var mutationStarted = CreateCompletionSource();
        await using var coordinator = new RecordingStorageCoordinator(
            getUsage: (_, _) => Task.FromResult(new RecordingStorageUsage(0, 0)),
            enforceLimit: async (_, _) =>
            {
                cleanupStarted.TrySetResult();
                await allowCleanupToFinish.Task;
                return new RecordingStorageCleanupResult(new RecordingStorageUsage(0, 0), 0, []);
            },
            NullLogger<RecordingStorageCoordinator>.Instance
        );
        coordinator.QueueRefreshOrRetention(Settings("recordings", maxUsageBytes: 100));
        await cleanupStarted.Task.WaitAsync(cancellationToken);

        var mutation = coordinator.ExecuteExclusiveAsync(
            _ =>
            {
                mutationStarted.TrySetResult();
                return Task.CompletedTask;
            },
            cancellationToken
        );

        Assert.False(mutationStarted.Task.IsCompleted);

        allowCleanupToFinish.TrySetResult();
        await mutation;
        Assert.True(mutationStarted.Task.IsCompletedSuccessfully);
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
