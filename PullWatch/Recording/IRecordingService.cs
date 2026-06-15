namespace PullWatch;

public interface IRecordingService : IAsyncDisposable
{
    event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    Task StartAsync(RecordingContext context, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
