using System.Collections.Concurrent;
using System.IO;
using GameFlowStudio.Storage.Serialization;

namespace GameFlowStudio.Desktop.Logging;

internal interface ILogSink
{
    void Write(DisplayLogEntry entry);
}

internal sealed class BoundedMemoryLogSink(int capacity) : ILogSink
{
    private readonly ConcurrentQueue<DisplayLogEntry> _entries = new();
    public IReadOnlyCollection<DisplayLogEntry> Entries => _entries.ToArray();

    public void Write(DisplayLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > capacity) _entries.TryDequeue(out _);
    }
}

internal sealed class DailyFileLogSink : ILogSink
{
    private readonly object _fileLock = new();

    public void Write(DisplayLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.LogsDirectory);
            var path = Path.Combine(StoragePaths.LogsDirectory, $"{entry.Timestamp:yyyy-MM-dd}.log");
            var line = $"{entry.Timestamp:O}\t{entry.Level}\t{entry.Category}\t{entry.Message}\t{entry.Exception}{Environment.NewLine}";
            lock (_fileLock) File.AppendAllText(path, line);
        }
        catch { /* 文件日志失败不能影响 UI 和工作流。 */ }
    }
}

internal sealed class EventLogSink : ILogSink
{
    public event EventHandler<DisplayLogEntry>? EntryAdded;
    public void Write(DisplayLogEntry entry) => EntryAdded?.Invoke(this, entry);
}
