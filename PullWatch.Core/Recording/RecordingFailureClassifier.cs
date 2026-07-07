namespace PullWatch;

public static class RecordingFailureClassifier
{
    public static bool IsTargetUnavailable(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception is CaptureTargetUnavailableException)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.InnerExceptions.Any(IsTargetUnavailable);
        }

        return IsTargetUnavailable(exception.InnerException);
    }

    public static bool IsOutputUnavailable(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception is RecordingOutputUnavailableException)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.InnerExceptions.Any(IsOutputUnavailable);
        }

        return IsOutputUnavailable(exception.InnerException);
    }
}
