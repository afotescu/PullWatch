using Microsoft.Extensions.Logging;

namespace PullWatch;

internal sealed class ApplicationStatusPublisher(
    ApplicationStatus initialStatus,
    ILogger<ApplicationStatusPublisher> logger
)
{
    private readonly object _notificationLock = new();
    private Task _notificationQueue = Task.CompletedTask;
    private ApplicationStatus _status = initialStatus;

    public event Action<ApplicationStatus>? StatusChanged;

    public ApplicationStatus Status => Volatile.Read(ref _status);

    public void Set(ApplicationStatus status)
    {
        lock (_notificationLock)
        {
            Volatile.Write(ref _status, status);
            QueueStatusChangedCore(status);
        }
    }

    public void Update(Func<ApplicationStatus, ApplicationStatus> update)
    {
        ApplicationStatus snapshot;

        lock (_notificationLock)
        {
            snapshot = update(Status);
            Volatile.Write(ref _status, snapshot);
            QueueStatusChangedCore(snapshot);
        }
    }

    private void QueueStatusChangedCore(ApplicationStatus snapshot)
    {
        _notificationQueue = _notificationQueue.ContinueWith(
            _ => NotifyStatusChanged(snapshot),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default
        );
    }

    private void NotifyStatusChanged(ApplicationStatus snapshot)
    {
        var handlers = StatusChanged;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<ApplicationStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Application status subscriber failed");
            }
        }
    }
}
