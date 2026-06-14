namespace PullWatch;

public interface IRecordingService : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
