namespace PullWatch;

public interface IWowProcessMonitor
{
    event Action<WowProcessStatus>? StatusChanged;

    WowProcessStatus Status { get; }

    Task WatchAsync(CancellationToken cancellationToken);
}
