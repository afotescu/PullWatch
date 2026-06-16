namespace PullWatch;

internal sealed class RecordingTargetUnavailableException(string message) : InvalidOperationException(message);
