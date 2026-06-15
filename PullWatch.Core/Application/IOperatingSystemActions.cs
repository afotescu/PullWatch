namespace PullWatch;

public interface IOperatingSystemActions
{
    Task OpenRecordingsFolderAsync(CancellationToken cancellationToken);
}
