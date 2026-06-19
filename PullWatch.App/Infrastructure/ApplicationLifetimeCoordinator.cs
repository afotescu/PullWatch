namespace PullWatch;

public sealed class ApplicationLifetimeCoordinator
{
    private readonly Func<ApplicationStatus> _getStatus;
    private readonly Func<bool> _confirmExitWhileRecording;
    private readonly Func<CancellationToken, Task<RecordingCommandResult>> _finalizeRecording;
    private readonly Action _requestShutdown;

    public ApplicationLifetimeCoordinator(
        Func<ApplicationStatus> getStatus,
        Func<bool> confirmExitWhileRecording,
        Func<CancellationToken, Task<RecordingCommandResult>> finalizeRecording,
        Action requestShutdown
    )
    {
        _getStatus = getStatus;
        _confirmExitWhileRecording = confirmExitWhileRecording;
        _finalizeRecording = finalizeRecording;
        _requestShutdown = requestShutdown;
    }

    public bool IsExitRequested { get; private set; }

    public bool ShouldHideOnWindowClose => !IsExitRequested;

    public RecordingCommandResult? LastFinalizationResult { get; private set; }

    public void BeginForcedExit()
    {
        IsExitRequested = true;
    }

    public async Task<bool> RequestExplicitExitAsync(CancellationToken cancellationToken)
    {
        LastFinalizationResult = null;
        var recordingState = _getStatus().Recording.State;

        if (recordingState != RecordingCoordinatorState.Idle)
        {
            if (!_confirmExitWhileRecording())
            {
                return false;
            }

            var result = await _finalizeRecording(cancellationToken);
            LastFinalizationResult = result;

            if (
                result
                is not RecordingCommandResult.Stopped
                    and not RecordingCommandResult.NoActiveRecording
            )
            {
                return false;
            }
        }

        IsExitRequested = true;
        _requestShutdown();
        return true;
    }
}
