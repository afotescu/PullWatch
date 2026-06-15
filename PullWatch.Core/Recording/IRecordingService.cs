namespace PullWatch;

public interface IRecordingService : IAsyncDisposable
{
    event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    event EventHandler? CaptureTargetExited;

    string? ActiveOutputPath { get; }

    Task StartAsync(RecordingContext context, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
