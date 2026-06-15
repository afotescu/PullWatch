namespace PullWatch;

public interface IRecordingService : IAsyncDisposable
{
    event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    string? ActiveOutputPath { get; }

    Task StartAsync(RecordingContext context, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
