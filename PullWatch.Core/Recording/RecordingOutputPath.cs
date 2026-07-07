namespace PullWatch;

internal static class RecordingOutputPath
{
    public static string Create(RecordingContext context, PullWatchSettings settings)
    {
        var recordingsDirectory =
            settings.RecordingsDirectory
            ?? throw new InvalidOperationException("Recordings directory was not configured.");

        try
        {
            Directory.CreateDirectory(recordingsDirectory);
        }
        catch (Exception exception) when (IsDirectoryUnavailableException(exception))
        {
            throw new RecordingOutputUnavailableException(recordingsDirectory, exception);
        }

        return RecordingFilenameBuilder.CreateAvailablePath(recordingsDirectory, context);
    }

    private static bool IsDirectoryUnavailableException(Exception exception)
    {
        return exception
            is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or DirectoryNotFoundException;
    }
}
