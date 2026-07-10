namespace PullWatch;

internal interface ICombatLogMonitor
{
    event Action<CombatLogReaderStatus>? StatusChanged;

    CombatLogReaderStatus Status { get; }

    Task ReadAsync(
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken
    );
}
