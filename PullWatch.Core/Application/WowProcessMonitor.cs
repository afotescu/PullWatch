using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed class WowProcessMonitor : IWowProcessMonitor, IDisposable
{
    private const string WowProcessName = "Wow";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<WowProcessMonitor> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly object _statusLock = new();
    private readonly object _processLock = new();
    private readonly object _exitSignalLock = new();
    private WowProcessStatus _status = new(WowProcessState.WaitingForProcess, null, null, null);
    private Process? _observedProcess;
    private TaskCompletionSource _targetExited = CreateCompletionSource();
    private bool _disposed;

    public WowProcessMonitor(ILogger<WowProcessMonitor> logger, TimeSpan? pollInterval = null)
    {
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public event Action<WowProcessStatus>? StatusChanged;

    public WowProcessStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishStatus(DiscoverStatus());

            var exitTask = GetExitTask();
            var delayTask = Task.Delay(_pollInterval, cancellationToken);
            var completed = await Task.WhenAny(delayTask, exitTask);

            if (completed == exitTask)
            {
                ResetExitTask();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearObservedProcess();
    }

    private WowProcessStatus DiscoverStatus()
    {
        try
        {
            return DiscoverStatusCore();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not inspect the World of Warcraft process");
            return Status with { LastError = exception };
        }
    }

    private WowProcessStatus DiscoverStatusCore()
    {
        var processes = Process.GetProcessesByName(WowProcessName);
        Process? selectedProcess = null;
        int? firstProcessId = null;

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    firstProcessId ??= process.Id;
                    var windowHandle = process.MainWindowHandle;

                    if (windowHandle == nint.Zero)
                    {
                        continue;
                    }

                    selectedProcess = process;
                    var title = process.MainWindowTitle;
                    TrackObservedProcess(selectedProcess);
                    return new WowProcessStatus(
                        WowProcessState.WindowAvailable,
                        selectedProcess.Id,
                        string.IsNullOrWhiteSpace(title) ? null : title,
                        null
                    );
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being inspected.
                }
            }

            ClearObservedProcess();
            return firstProcessId is null
                ? new WowProcessStatus(WowProcessState.WaitingForProcess, null, null, null)
                : new WowProcessStatus(
                    WowProcessState.WaitingForWindow,
                    firstProcessId,
                    null,
                    null
                );
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!ReferenceEquals(process, selectedProcess))
                {
                    process.Dispose();
                }
            }
        }
    }

    private void TrackObservedProcess(Process process)
    {
        lock (_processLock)
        {
            ClearObservedProcessCore();
            _observedProcess = process;
            _observedProcess.Exited += OnObservedProcessExited;
            _observedProcess.EnableRaisingEvents = true;
        }
    }

    private void ClearObservedProcess()
    {
        lock (_processLock)
        {
            ClearObservedProcessCore();
        }
    }

    private void ClearObservedProcessCore()
    {
        if (_observedProcess is null)
        {
            return;
        }

        try
        {
            _observedProcess.Exited -= OnObservedProcessExited;
        }
        finally
        {
            _observedProcess.Dispose();
            _observedProcess = null;
        }
    }

    private void OnObservedProcessExited(object? sender, EventArgs eventArgs)
    {
        lock (_exitSignalLock)
        {
            _targetExited.TrySetResult();
        }
    }

    private Task GetExitTask()
    {
        lock (_exitSignalLock)
        {
            return _targetExited.Task;
        }
    }

    private void ResetExitTask()
    {
        lock (_exitSignalLock)
        {
            if (_targetExited.Task.IsCompleted)
            {
                _targetExited = CreateCompletionSource();
            }
        }
    }

    private void PublishStatus(WowProcessStatus status)
    {
        Action<WowProcessStatus>? handlers;

        lock (_statusLock)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
            handlers = StatusChanged;
        }

        handlers?.Invoke(status);
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
