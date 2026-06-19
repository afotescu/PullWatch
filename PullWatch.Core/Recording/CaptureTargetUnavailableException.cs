namespace PullWatch;

public sealed class CaptureTargetUnavailableException : InvalidOperationException
{
    public CaptureTargetUnavailableException(string message)
        : base(message) { }
}
