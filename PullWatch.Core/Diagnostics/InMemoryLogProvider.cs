using Microsoft.Extensions.Logging;

namespace PullWatch;

public sealed record ApplicationLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? Exception);

public sealed class InMemoryLogProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly Queue<ApplicationLogEntry> _entries;
    private readonly int _capacity;
    private bool _disposed;

    public InMemoryLogProvider(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Queue<ApplicationLogEntry>(capacity);
    }

    public event Action? LogsChanged;

    public IReadOnlyList<ApplicationLogEntry> GetSnapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new InMemoryLogger(this, categoryName);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void Add(ApplicationLogEntry entry)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        var handlers = LogsChanged;

        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
                // Diagnostics observers must never make application logging fail.
            }
        }
    }

    private sealed class InMemoryLogger(InMemoryLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Add(new ApplicationLogEntry(
                DateTimeOffset.Now,
                logLevel,
                category,
                eventId,
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
