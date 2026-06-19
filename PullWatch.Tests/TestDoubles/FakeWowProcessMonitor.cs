namespace PullWatch.Tests.TestDoubles;

internal sealed class FakeWowProcessMonitor : IWowProcessMonitor
{
    private WowProcessStatus _status;
    private TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeWowProcessMonitor()
        : this(new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null)) { }

    public FakeWowProcessMonitor(WowProcessStatus status)
    {
        _status = status;
    }

    public event Action<WowProcessStatus>? StatusChanged;

    public WowProcessStatus Status => _status;

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        Started = true;

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            Stopped = true;
            _stopped.TrySetResult();
        }
    }

    public void Publish(WowProcessStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public Task WaitForStoppedAsync()
    {
        return _stopped.Task;
    }
}
