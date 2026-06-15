namespace PullWatch.Tests.TestDoubles;

internal sealed class FakeCombatLogMonitor : ICombatLogMonitor
{
    private CombatLogReaderStatus _status = new(
        CombatLogReaderState.WaitingForCombatLog,
        null,
        null,
        null);

    public event Action<CombatLogReaderStatus>? StatusChanged;

    public CombatLogReaderStatus Status => _status;

    public bool Started { get; private set; }

    public async Task ReadAsync(
        Func<CombatLogEvent, CancellationToken, Task> handleEventAsync,
        CancellationToken cancellationToken)
    {
        Started = true;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public void Publish(CombatLogReaderStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }
}
