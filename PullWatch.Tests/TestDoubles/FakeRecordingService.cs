namespace PullWatch.Tests.TestDoubles;

internal sealed class FakeRecordingService : IRecordingService
{
    public List<string> Calls { get; } = [];

    public Exception? StartException { get; set; }

    public Exception? StopException { get; set; }

    public TaskCompletionSource? PendingStart { get; set; }

    public TaskCompletionSource? PendingStop { get; set; }

    public event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Calls.Add("start");

        if (StartException is not null)
        {
            return Task.FromException(StartException);
        }

        return PendingStart?.Task ?? Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Calls.Add("stop");

        if (StopException is not null)
        {
            return Task.FromException(StopException);
        }

        return PendingStop?.Task ?? Task.CompletedTask;
    }

    public void RaiseFailure(Exception exception)
    {
        Failed?.Invoke(this, new RecordingServiceFailedEventArgs(exception));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
