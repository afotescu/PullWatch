using System.IO.Pipes;

namespace PullWatch.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondInstanceActivationReachesExistingInstance()
    {
        var name = $"PullWatch.Tests.{Guid.NewGuid():N}";
        await using var first = new SingleInstanceCoordinator(name);
        await using var second = new SingleInstanceCoordinator(name);
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Assert.True(first.TryAcquire());
        first.StartActivationListener(
            (_, _) =>
            {
                activated.TrySetResult();
                return Task.FromResult(SingleInstanceActivationResult.ActivatedExisting);
            }
        );
        Assert.False(second.TryAcquire());

        Assert.Equal(
            SingleInstanceActivationResult.ActivatedExisting,
            await second.ActivateExistingAsync(TestContext.Current.CancellationToken)
        );
        await activated.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task UpgradeActivationResponseReachesSecondInstance()
    {
        var name = $"PullWatch.Tests.{Guid.NewGuid():N}";
        await using var first = new SingleInstanceCoordinator(name);
        await using var second = new SingleInstanceCoordinator(name);
        var exchangeCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Assert.True(first.TryAcquire());
        first.StartActivationListener(
            (request, _) =>
            {
                Assert.Equal("2.0.0", request.AppVersion);
                Assert.Equal(@"C:\Apps\PullWatch\PullWatch.exe", request.ExecutablePath);
                return Task.FromResult(SingleInstanceActivationResult.UpgradeAccepted);
            },
            (result, responseSent) =>
            {
                if (result != SingleInstanceActivationResult.UpgradeAccepted)
                {
                    exchangeCompleted.TrySetException(
                        new InvalidOperationException($"Unexpected result: {result}.")
                    );
                    return;
                }

                exchangeCompleted.TrySetResult(responseSent);
            }
        );
        Assert.False(second.TryAcquire());

        var result = await second.ActivateExistingAsync(
            new SingleInstanceLaunchRequest("2.0.0", @"C:\Apps\PullWatch\PullWatch.exe"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(SingleInstanceActivationResult.UpgradeAccepted, result);
        Assert.True(
            await exchangeCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task UpgradeRequesterCanAcquireAfterFirstInstanceReleasesLock()
    {
        var name = $"PullWatch.Tests.{Guid.NewGuid():N}";
        var first = new SingleInstanceCoordinator(name);
        await using var second = new SingleInstanceCoordinator(name);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());

        var waitTask = second.WaitForReleaseAndAcquireAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken
        );

        await first.DisposeAsync();

        Assert.True(await waitTask);
        Assert.True(second.TryAcquire());
    }

    [Fact]
    public async Task LegacyActivationSignalReachesProtocolListener()
    {
        var name = $"PullWatch.Tests.{Guid.NewGuid():N}";
        await using var first = new SingleInstanceCoordinator(name);
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Assert.True(first.TryAcquire());
        first.StartActivationListener(
            (request, _) =>
            {
                Assert.Null(request.AppVersion);
                Assert.Null(request.ExecutablePath);
                activated.TrySetResult();
                return Task.FromResult(SingleInstanceActivationResult.ActivatedExisting);
            }
        );

        await using (
            var legacyClient = new NamedPipeClientStream(
                ".",
                name,
                PipeDirection.Out,
                PipeOptions.Asynchronous
            )
        )
        {
            await legacyClient.ConnectAsync(TestContext.Current.CancellationToken);
            await legacyClient.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        }

        await activated.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task DisposalDoesNotWaitForCapturedSynchronizationContext()
    {
        var coordinator = new SingleInstanceCoordinator($"PullWatch.Tests.{Guid.NewGuid():N}");
        var previousContext = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new NonPumpingSynchronizationContext()
            );
            Assert.True(coordinator.TryAcquire());
            coordinator.StartActivationListener(
                (_, _) => Task.FromResult(SingleInstanceActivationResult.ActivatedExisting)
            );
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await Task.Run(
                () => coordinator.DisposeAsync().AsTask(),
                TestContext.Current.CancellationToken
            )
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListenerContinuesAfterClientDisconnectsWithoutSendingActivation()
    {
        var name = $"PullWatch.Tests.{Guid.NewGuid():N}";
        await using var first = new SingleInstanceCoordinator(name);
        await using var second = new SingleInstanceCoordinator(name);
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Assert.True(first.TryAcquire());
        first.StartActivationListener(
            (_, _) =>
            {
                activated.TrySetResult();
                return Task.FromResult(SingleInstanceActivationResult.ActivatedExisting);
            }
        );
        Assert.False(second.TryAcquire());

        await using (
            var incompleteClient = new NamedPipeClientStream(
                ".",
                name,
                PipeDirection.Out,
                PipeOptions.Asynchronous
            )
        )
        {
            await incompleteClient.ConnectAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            SingleInstanceActivationResult.ActivatedExisting,
            await second.ActivateExistingAsync(TestContext.Current.CancellationToken)
        );
        await activated.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken
        );
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }
}
