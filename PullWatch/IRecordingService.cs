namespace PullWatch;

public interface IRecordingService : IAsyncDisposable
{
    event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
