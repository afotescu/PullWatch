namespace PullWatch;

public sealed class RecordingOutputUnavailableException : InvalidOperationException
{
    public RecordingOutputUnavailableException(string recordingsDirectory, Exception innerException)
        : base(
            "Recording cannot start because the recordings folder is unavailable. Choose a writable folder in Settings.",
            innerException
        )
    {
        RecordingsDirectory = recordingsDirectory;
    }

    public string RecordingsDirectory { get; }
}
