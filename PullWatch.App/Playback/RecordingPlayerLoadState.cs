namespace PullWatch;

internal enum RecordingPlayerLoadStatus
{
    Idle,
    Scheduled,
    Opening,
    Open,
    Failed,
}

internal readonly record struct RecordingPlayerLoadRequest(int Version, int Attempt, Uri Source);

internal sealed class RecordingPlayerLoadState
{
    private int _attempt;
    private Uri? _source;
    private int _version;

    public RecordingPlayerLoadStatus Status { get; private set; }

    public RecordingPlayerLoadRequest? PendingRequest =>
        Status == RecordingPlayerLoadStatus.Scheduled && _source is not null
            ? new RecordingPlayerLoadRequest(_version, _attempt, _source)
            : null;

    public RecordingPlayerLoadRequest? Request(Uri source, bool retryFailed = false)
    {
        var isSameSource = SourcesEqual(_source, source);
        if (
            isSameSource
            && (
                Status
                    is RecordingPlayerLoadStatus.Scheduled
                        or RecordingPlayerLoadStatus.Opening
                        or RecordingPlayerLoadStatus.Open
                || (Status == RecordingPlayerLoadStatus.Failed && !retryFailed)
            )
        )
        {
            return null;
        }

        _attempt = isSameSource && retryFailed ? _attempt + 1 : 1;
        _source = source;
        Status = RecordingPlayerLoadStatus.Scheduled;
        return new RecordingPlayerLoadRequest(++_version, _attempt, source);
    }

    public bool TryStart(RecordingPlayerLoadRequest request)
    {
        if (!IsCurrent(request) || Status != RecordingPlayerLoadStatus.Scheduled)
        {
            return false;
        }

        Status = RecordingPlayerLoadStatus.Opening;
        return true;
    }

    public bool TryComplete(RecordingPlayerLoadRequest request, bool succeeded)
    {
        if (!IsCurrent(request) || Status != RecordingPlayerLoadStatus.Opening)
        {
            return false;
        }

        Status = succeeded ? RecordingPlayerLoadStatus.Open : RecordingPlayerLoadStatus.Failed;
        return true;
    }

    public int Clear()
    {
        _attempt = 0;
        _source = null;
        Status = RecordingPlayerLoadStatus.Idle;
        return ++_version;
    }

    public bool IsCurrent(RecordingPlayerLoadRequest request)
    {
        return request.Version == _version && SourcesEqual(_source, request.Source);
    }

    private static bool SourcesEqual(Uri? left, Uri right)
    {
        if (left is null || left.IsFile != right.IsFile)
        {
            return false;
        }

        return left.IsFile
            ? string.Equals(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase)
            : Uri.Compare(
                left,
                right,
                UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase
            ) == 0;
    }
}
