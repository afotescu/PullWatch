namespace PullWatch.Tests.TestDoubles;

internal sealed class FakeRecordingService : IRecordingService
{
    public List<string> Calls { get; } = [];

    public List<RecordingContext> StartedContexts { get; } = [];

    public Exception? StartException { get; set; }

    public Exception? StopException { get; set; }

    public TaskCompletionSource? PendingStart { get; set; }

    public TaskCompletionSource? PendingStop { get; set; }

    public string? ActiveOutputPath { get; set; } = @"C:\Recordings\active.mp4";

    public event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    public event EventHandler? CaptureTargetExited;

    public Task StartAsync(RecordingContext context, CancellationToken cancellationToken)
    {
        Calls.Add("start");
        StartedContexts.Add(context);

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

    public void RaiseCaptureTargetExited()
    {
        CaptureTargetExited?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
