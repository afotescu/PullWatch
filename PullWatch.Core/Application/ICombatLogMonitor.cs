namespace PullWatch;

public interface ICombatLogMonitor
{
    event Action<CombatLogReaderStatus>? StatusChanged;

    CombatLogReaderStatus Status { get; }

    Task ReadAsync(
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken
    );
}
