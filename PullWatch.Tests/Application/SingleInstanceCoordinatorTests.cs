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
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(first.TryAcquire());
        first.StartActivationListener(() => activated.TrySetResult());
        Assert.False(second.TryAcquire());

        Assert.True(await second.ActivateExistingAsync(TestContext.Current.CancellationToken));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposalDoesNotWaitForCapturedSynchronizationContext()
    {
        var coordinator = new SingleInstanceCoordinator($"PullWatch.Tests.{Guid.NewGuid():N}");
        var previousContext = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            Assert.True(coordinator.TryAcquire());
            coordinator.StartActivationListener(() => { });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await Task.Run(
                () => coordinator.DisposeAsync().AsTask(),
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListenerContinuesAfterClientDisconnectsWithoutSendingActivation()
    {
        var name = $"PullWatch.Tests.{Guid.NewGuid():N}";
        await using var first = new SingleInstanceCoordinator(name);
        await using var second = new SingleInstanceCoordinator(name);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(first.TryAcquire());
        first.StartActivationListener(() => activated.TrySetResult());
        Assert.False(second.TryAcquire());

        await using (var incompleteClient = new NamedPipeClientStream(
                         ".",
                         name,
                         PipeDirection.Out,
                         PipeOptions.Asynchronous))
        {
            await incompleteClient.ConnectAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(await second.ActivateExistingAsync(TestContext.Current.CancellationToken));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }
    }
}
