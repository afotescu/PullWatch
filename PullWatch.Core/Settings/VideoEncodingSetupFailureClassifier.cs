namespace PullWatch;

public static class VideoEncodingSetupFailureClassifier
{
    public static bool IsSetupFailure(Exception? exception)
    {
        return TryGetMessage(exception) is not null;
    }

    public static string? TryGetMessage(Exception? exception)
    {
        var message = exception?.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return
            message.StartsWith("Video encoding needs to be tested", StringComparison.Ordinal)
            || message.StartsWith("Video encoding needs to be retested", StringComparison.Ordinal)
            || message.StartsWith("Video encoding must be calibrated", StringComparison.Ordinal)
            || message.StartsWith(
                "No tested video encoder profile has been selected",
                StringComparison.Ordinal
            )
            || message.StartsWith(
                "The selected video encoder profile has not been tested",
                StringComparison.Ordinal
            )
            || message.StartsWith(
                "The selected video encoder profile did not pass testing",
                StringComparison.Ordinal
            )
            ? message
            : null;
    }
}
