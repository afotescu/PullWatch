namespace PullWatch.Tests.TestDoubles;

internal sealed class FakeRecordingService : IRecordingService
{
    public List<string> Calls { get; } = [];

    public Exception? StartException { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Calls.Add("start");

        return StartException is null
            ? Task.CompletedTask
            : Task.FromException(StartException);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Calls.Add("stop");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
