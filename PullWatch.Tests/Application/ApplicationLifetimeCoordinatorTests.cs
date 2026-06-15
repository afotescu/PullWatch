namespace PullWatch.Tests;

public sealed class ApplicationLifetimeCoordinatorTests
{
    [Fact]
    public void WindowCloseHidesUntilExitIsRequested()
    {
        var coordinator = CreateCoordinator(Status(RecordingCoordinatorState.Idle));

        Assert.True(coordinator.ShouldHideOnWindowClose);

        coordinator.BeginForcedExit();

        Assert.False(coordinator.ShouldHideOnWindowClose);
    }

    [Fact]
    public async Task ExplicitExitFinalizesActiveRecordingAndRequestsShutdown()
    {
        var finalized = false;
        var shutdown = false;
        var coordinator = CreateCoordinator(
            Status(RecordingCoordinatorState.Recording),
            confirm: () => true,
            finalize: _ =>
            {
                finalized = true;
                return Task.FromResult(RecordingCommandResult.Stopped);
            },
            shutdown: () => shutdown = true);

        var result = await coordinator.RequestExplicitExitAsync(TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(finalized);
        Assert.True(shutdown);
        Assert.False(coordinator.ShouldHideOnWindowClose);
    }

    [Fact]
    public async Task ExitCancellationLeavesApplicationRunning()
    {
        var finalized = false;
        var shutdown = false;
        var coordinator = CreateCoordinator(
            Status(RecordingCoordinatorState.Recording),
            confirm: () => false,
            finalize: _ =>
            {
                finalized = true;
                return Task.FromResult(RecordingCommandResult.Stopped);
            },
            shutdown: () => shutdown = true);

        var result = await coordinator.RequestExplicitExitAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(finalized);
        Assert.False(shutdown);
        Assert.True(coordinator.ShouldHideOnWindowClose);
    }

    [Theory]
    [InlineData(RecordingCommandResult.Failed)]
    [InlineData(RecordingCommandResult.TimedOut)]
    public async Task FailedFinalizationLeavesApplicationRunning(RecordingCommandResult finalizationResult)
    {
        var shutdown = false;
        var coordinator = CreateCoordinator(
            Status(RecordingCoordinatorState.Recording),
            confirm: () => true,
            finalize: _ => Task.FromResult(finalizationResult),
            shutdown: () => shutdown = true);

        var result = await coordinator.RequestExplicitExitAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(shutdown);
        Assert.True(coordinator.ShouldHideOnWindowClose);
        Assert.Equal(finalizationResult, coordinator.LastFinalizationResult);
    }

    private static ApplicationLifetimeCoordinator CreateCoordinator(
        ApplicationStatus status,
        Func<bool>? confirm = null,
        Func<CancellationToken, Task<RecordingCommandResult>>? finalize = null,
        Action? shutdown = null)
    {
        return new ApplicationLifetimeCoordinator(
            () => status,
            confirm ?? (() => true),
            finalize ?? (_ => Task.FromResult(RecordingCommandResult.NoActiveRecording)),
            shutdown ?? (() => { }));
    }

    private static ApplicationStatus Status(RecordingCoordinatorState state)
    {
        return new ApplicationStatus(
            new PullWatchSettings(),
            new RecordingCoordinatorStatus(
                state,
                state == RecordingCoordinatorState.Idle ? null : RecordingOwner.Manual,
                null,
                state == RecordingCoordinatorState.Idle
                    ? null
                    : new ManualRecordingContext(DateTimeOffset.Now),
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
}
