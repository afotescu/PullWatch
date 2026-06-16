namespace PullWatch;

internal sealed class LazyRecordingService(Func<IRecordingService> createRecordingService)
    : IRecordingService
{
    private readonly object _lock = new();
    private IRecordingService? _recordingService;
    private bool _disposed;

    public event EventHandler<RecordingServiceFailedEventArgs>? Failed;

    public event EventHandler? CaptureTargetExited;

    public string? ActiveOutputPath
    {
        get
        {
            lock (_lock)
            {
                return _recordingService?.ActiveOutputPath;
            }
        }
    }

    public Task StartAsync(RecordingContext context, CancellationToken cancellationToken)
    {
        return GetOrCreate().StartAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IRecordingService? recordingService;

        lock (_lock)
        {
            recordingService = _recordingService;
        }

        return recordingService?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        IRecordingService? recordingService;

        lock (_lock)
        {
            _disposed = true;
            recordingService = _recordingService;
            _recordingService = null;
        }

        if (recordingService is null)
        {
            return;
        }

        recordingService.Failed -= OnFailed;
        recordingService.CaptureTargetExited -= OnCaptureTargetExited;
        await recordingService.DisposeAsync();
    }

    private IRecordingService GetOrCreate()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recordingService is not null)
            {
                return _recordingService;
            }

            var recordingService = createRecordingService();
            recordingService.Failed += OnFailed;
            recordingService.CaptureTargetExited += OnCaptureTargetExited;
            _recordingService = recordingService;
            return recordingService;
        }
    }

    private void OnFailed(object? sender, RecordingServiceFailedEventArgs eventArgs)
    {
        Failed?.Invoke(this, eventArgs);
    }

    private void OnCaptureTargetExited(object? sender, EventArgs eventArgs)
    {
        CaptureTargetExited?.Invoke(this, eventArgs);
    }
}
