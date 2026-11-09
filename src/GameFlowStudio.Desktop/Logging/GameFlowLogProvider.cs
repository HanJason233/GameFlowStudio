using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.Logging;

public sealed record DisplayLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? Exception)
{
    public string DisplayText => $"{Timestamp:HH:mm:ss.fff} [{Level}] {Message}";
}

/// <summary>同时向 UI 事件流和每日文本文件写入日志。</summary>
public sealed class GameFlowLogProvider : ILoggerProvider
{
    private readonly BoundedMemoryLogSink _memory = new(1000);
    private readonly EventLogSink _events = new();
    private readonly IReadOnlyList<ILogSink> _sinks;

    public GameFlowLogProvider() => _sinks = [_memory, new DailyFileLogSink(), _events];
    public event EventHandler<DisplayLogEntry>? EntryAdded { add => _events.EntryAdded += value; remove => _events.EntryAdded -= value; }
    public IReadOnlyCollection<DisplayLogEntry> Entries => _memory.Entries;
    public ILogger CreateLogger(string categoryName) => new GameFlowLogger(this, categoryName);
    public void Dispose() { }

    private void Write(DisplayLogEntry entry) { foreach (var sink in _sinks) sink.Write(entry); }

    private sealed class GameFlowLogger(GameFlowLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(new(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
