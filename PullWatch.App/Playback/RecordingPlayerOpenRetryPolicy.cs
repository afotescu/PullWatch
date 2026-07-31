namespace PullWatch;

internal static class RecordingPlayerOpenRetryPolicy
{
    private const string MissingPlaylistItemsError = "No playlist items were found";
    private const int MaximumAttempts = 4;

    public static bool ShouldRetry(RecordingPlayerLoadRequest request, string? error)
    {
        return request.Attempt < MaximumAttempts && IsMissingPlaylistItemsError(error);
    }

    public static bool IsMissingPlaylistItemsError(string? error)
    {
        return string.Equals(
            error?.Trim(),
            MissingPlaylistItemsError,
            StringComparison.OrdinalIgnoreCase
        );
    }

    public static TimeSpan GetDelay(RecordingPlayerLoadRequest request)
    {
        return request.Attempt switch
        {
            1 => TimeSpan.FromMilliseconds(250),
            2 => TimeSpan.FromMilliseconds(750),
            _ => TimeSpan.FromMilliseconds(1500),
        };
    }
}
