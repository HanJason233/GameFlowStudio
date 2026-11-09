using System.Collections.Concurrent;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Runtime;

public sealed class RuntimeLogStore : ILoggerProvider
{
    private readonly ConcurrentQueue<RuntimeLogInfo> _entries = new();
    private long _sequence;
    public event EventHandler<RuntimeLogInfo>? EntryAdded;

    public IReadOnlyList<RuntimeLogInfo> GetEntries(long afterSequence = 0) =>
        _entries.Where(entry => entry.Sequence > afterSequence).ToArray();

    public ILogger CreateLogger(string categoryName) => new RuntimeLogger(this, categoryName);
    public void Dispose() { }

    private void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var entry = new RuntimeLogInfo(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow,
            level.ToString(), category, message, exception?.ToString());
        _entries.Enqueue(entry);
        while (_entries.Count > 2000) _entries.TryDequeue(out _);
        EntryAdded?.Invoke(this, entry);
    }

    private sealed class RuntimeLogger(RuntimeLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) store.Add(logLevel, category, formatter(state, exception), exception);
        }
    }
}
