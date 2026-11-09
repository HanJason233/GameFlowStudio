using System.IO;
using System.Threading.Channels;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Protocol;

namespace GameFlowStudio.Runtime;

/// <summary>Sequences, journals, and sends execution events independently of the current pipe connection.</summary>
public sealed class RuntimeEventPublisher : IAsyncDisposable
{
    private const int MaximumHistory = 4096;
    private const int MaximumPendingLiveEvents = 1024;
    private readonly object _sync = new();
    private readonly Queue<RuntimeEventInfo> _history = new();
    private readonly Dictionary<(string ScriptId, string NodeId), RuntimeNodeSnapshot> _nodeStates = new();
    private readonly HashSet<string> _visitedScripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _scriptNodeCounts = new(StringComparer.Ordinal);
    private readonly Channel<RuntimeEventInfo> _outbound = Channel.CreateBounded<RuntimeEventInfo>(new BoundedChannelOptions(MaximumPendingLiveEvents)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sendTask;
    private PipeMessageChannel? _channel;
    private string? _runId;
    private string? _currentScriptId;
    private string? _currentNodeId;
    private string? _currentNodeName;
    private WorkflowExecutionState _state = WorkflowExecutionState.Idle;
    private long _sequence;
    private long _currentNodeSequence;
    private long _lastProgressTimestamp = DateTimeOffset.UtcNow.UtcTicks;
    private int _totalNodes;
    private int _completedNodes;
    private bool _disposed;

    public RuntimeEventPublisher() => _sendTask = Task.Run(SendLoopAsync);

    public string? CurrentRunId { get { lock (_sync) return _runId; } }
    public string? CurrentNodeId { get { lock (_sync) return _currentNodeId; } }
    public string? CurrentNodeName { get { lock (_sync) return _currentNodeName; } }
    public long LastSequence { get { lock (_sync) return _sequence; } }
    public DateTimeOffset LastProgressAt => new(Interlocked.Read(ref _lastProgressTimestamp), TimeSpan.Zero);

    public void Attach(PipeMessageChannel channel)
    {
        lock (_sync) _channel = channel;
    }

    public void Detach(PipeMessageChannel channel)
    {
        lock (_sync)
            if (ReferenceEquals(_channel, channel)) _channel = null;
    }

    public void SetProject(AutomationProject project)
    {
        lock (_sync)
        {
            _runId = null;
            _currentScriptId = project.ActiveScriptId;
            _currentNodeId = null;
            _currentNodeName = null;
            _state = WorkflowExecutionState.Idle;
            _completedNodes = 0;
            _totalNodes = 0;
            _nodeStates.Clear();
            _visitedScripts.Clear();
            _scriptNodeCounts.Clear();
            foreach (var script in project.Scripts)
                _scriptNodeCounts[script.Id] = script.Nodes.Count;
            if (_currentScriptId is { } active && _scriptNodeCounts.TryGetValue(active, out var count))
            {
                _visitedScripts.Add(active);
                _totalNodes = count;
            }
            TouchProgressLocked();
        }
    }

    public void BeginRun(string runId, AutomationProject project, string? scriptId)
    {
        lock (_sync)
        {
            _runId = runId;
            _currentScriptId = scriptId;
            _currentNodeId = null;
            _currentNodeName = null;
            _currentNodeSequence = 0;
            _state = WorkflowExecutionState.Running;
            _completedNodes = 0;
            _totalNodes = 0;
            _nodeStates.Clear();
            _visitedScripts.Clear();
            _scriptNodeCounts.Clear();
            foreach (var script in project.Scripts)
                _scriptNodeCounts[script.Id] = script.Nodes.Count;
            if (scriptId is { } active && _scriptNodeCounts.TryGetValue(active, out var count))
            {
                _visitedScripts.Add(active);
                _totalNodes = count;
            }
            _totalNodes = Math.Max(1, _totalNodes);
            TouchProgressLocked();
        }
    }

    public RuntimeRunSnapshot GetSnapshot(IReadOnlyList<RuntimeLogInfo> logs)
    {
        lock (_sync)
        {
            var progress = _state == WorkflowExecutionState.Completed
                ? 100d
                : Math.Clamp(_completedNodes * 100d / Math.Max(1, _totalNodes), 0d, 99d);
            return new RuntimeRunSnapshot(
                _runId,
                _state.ToString(),
                _currentScriptId,
                _currentNodeId,
                _currentNodeName,
                _completedNodes,
                Math.Max(1, _totalNodes),
                progress,
                _sequence,
                _nodeStates.Values.OrderBy(node => node.Sequence).ToArray(),
                logs);
        }
    }

    public RuntimeEventsResponse GetEvents(long afterSequence)
    {
        lock (_sync)
        {
            var oldest = _history.Count == 0 ? _sequence + 1 : _history.Peek().Sequence;
            var truncated = afterSequence < oldest - 1 || afterSequence > _sequence;
            return new RuntimeEventsResponse(
                afterSequence,
                _sequence,
                truncated,
                _history.Where(item => item.Sequence > afterSequence).ToArray());
        }
    }

    public void ObserveRunner(IWorkflowRunner runner)
    {
        runner.NodeStarted += OnNodeStarted;
        runner.NodeCompleted += OnNodeCompleted;
        runner.NodeFailed += OnNodeFailed;
        runner.StateChanged += OnStateChanged;
    }

    public Task PublishRunFailedAsync(string runId, string error)
    {
        lock (_sync) PublishLocked(RuntimeEvents.RunFailed, new RuntimeEventPayload { RunId = runId, Error = error }, runId);
        return Task.CompletedTask;
    }

    public Task PublishLogAsync(RuntimeLogInfo entry)
    {
        lock (_sync) PublishLocked(RuntimeEvents.LogReceived, new RuntimeEventPayload { Log = entry });
        return Task.CompletedTask;
    }

    public Task PublishAsync(string eventType, RuntimeEventPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) PublishLocked(eventType, payload);
        return Task.CompletedTask;
    }

    private void OnNodeStarted(object? sender, NodeExecutionEventArgs args)
    {
        lock (_sync)
        {
            var scriptId = ResolveScriptLocked(args.ScriptId);
            VisitScriptLocked(scriptId);
            _currentScriptId = scriptId;
            _currentNodeId = args.Node.Id;
            _currentNodeName = args.Node.Data.DisplayName;
            TouchProgressLocked();
            var record = PublishLocked(RuntimeEvents.NodeStarted, new RuntimeEventPayload
            {
                ScriptId = scriptId,
                NodeId = args.Node.Id,
                NodeName = args.Node.Data.DisplayName,
                WindowHandle = args.WindowHandle?.ToInt64(),
                WindowTitle = args.WindowTitle
            });
            _currentNodeSequence = record.Sequence;
            _nodeStates[(scriptId, args.Node.Id)] = new RuntimeNodeSnapshot(scriptId, args.Node.Id,
                args.Node.Data.DisplayName, "Running", null, args.WindowHandle?.ToInt64(), args.WindowTitle, record.Sequence);
        }
    }

    private void OnNodeCompleted(object? sender, NodeExecutionEventArgs args)
    {
        lock (_sync)
        {
            var scriptId = ResolveScriptLocked(args.ScriptId);
            VisitScriptLocked(scriptId);
            _currentScriptId = scriptId;
            _currentNodeId = args.Node.Id;
            _currentNodeName = args.Node.Data.DisplayName;
            TouchProgressLocked();
            var record = PublishLocked(RuntimeEvents.NodeCompleted, new RuntimeEventPayload
            {
                ScriptId = scriptId,
                NodeId = args.Node.Id,
                NodeName = args.Node.Data.DisplayName,
                ElapsedMilliseconds = args.ElapsedMilliseconds,
                WindowHandle = args.WindowHandle?.ToInt64(),
                WindowTitle = args.WindowTitle
            });
            _completedNodes++;
            _nodeStates[(scriptId, args.Node.Id)] = new RuntimeNodeSnapshot(scriptId, args.Node.Id,
                args.Node.Data.DisplayName, "Completed", null, args.WindowHandle?.ToInt64(), args.WindowTitle, record.Sequence);
            var progress = Math.Clamp(_completedNodes * 100d / Math.Max(1, _totalNodes), 0d, 99d);
            PublishLocked(RuntimeEvents.ProgressChanged, new RuntimeEventPayload
            {
                ScriptId = scriptId,
                NodeId = args.Node.Id,
                CompletedNodes = _completedNodes,
                TotalNodes = Math.Max(1, _totalNodes),
                ProgressPercent = progress
            });
        }
    }

    private void OnNodeFailed(object? sender, NodeExecutionEventArgs args)
    {
        lock (_sync)
        {
            var scriptId = ResolveScriptLocked(args.ScriptId);
            VisitScriptLocked(scriptId);
            _currentScriptId = scriptId;
            _currentNodeId = args.Node.Id;
            _currentNodeName = args.Node.Data.DisplayName;
            TouchProgressLocked();
            var record = PublishLocked(RuntimeEvents.NodeFailed, new RuntimeEventPayload
            {
                ScriptId = scriptId,
                NodeId = args.Node.Id,
                NodeName = args.Node.Data.DisplayName,
                Error = args.Error,
                ElapsedMilliseconds = args.ElapsedMilliseconds,
                WindowHandle = args.WindowHandle?.ToInt64(),
                WindowTitle = args.WindowTitle
            });
            _nodeStates[(scriptId, args.Node.Id)] = new RuntimeNodeSnapshot(scriptId, args.Node.Id,
                args.Node.Data.DisplayName, "Failed", args.Error, args.WindowHandle?.ToInt64(), args.WindowTitle, record.Sequence);
        }
    }

    private void OnStateChanged(object? sender, WorkflowStateChangedEventArgs args)
    {
        lock (_sync)
        {
            _state = args.Current;
            if (args.Current == WorkflowExecutionState.Idle)
            {
                foreach (var pair in _nodeStates.Where(pair => pair.Value.State == "Running").ToArray())
                    _nodeStates[pair.Key] = pair.Value with { State = "Stopped" };
                _currentNodeId = null;
                _currentNodeName = null;
            }
            TouchProgressLocked();
            PublishLocked(RuntimeEvents.StateChanged, new RuntimeEventPayload
            {
                PreviousState = args.Previous.ToString(),
                State = args.Current.ToString()
            });
            if (args.Current == WorkflowExecutionState.Completed)
            {
                PublishLocked(RuntimeEvents.ProgressChanged, new RuntimeEventPayload
                {
                    CompletedNodes = _completedNodes,
                    TotalNodes = Math.Max(1, _totalNodes),
                    ProgressPercent = 100
                });
                PublishLocked(RuntimeEvents.RunCompleted, new RuntimeEventPayload { State = args.Current.ToString() });
            }
        }
    }

    private string ResolveScriptLocked(string? scriptId) => scriptId ?? _currentScriptId ?? string.Empty;

    private void VisitScriptLocked(string scriptId)
    {
        if (_visitedScripts.Add(scriptId) && _scriptNodeCounts.TryGetValue(scriptId, out var count))
            _totalNodes = Math.Max(1, _totalNodes + count);
    }

    private RuntimeEventInfo PublishLocked(string eventType, RuntimeEventPayload payload, string? runId = null)
    {
        var sequence = ++_sequence;
        var capturedRunId = runId ?? payload.RunId ?? _runId;
        var info = new RuntimeEventInfo(sequence, eventType, payload with { RunId = capturedRunId, Sequence = sequence });
        _history.Enqueue(info);
        while (_history.Count > MaximumHistory) _history.Dequeue();
        _outbound.Writer.TryWrite(info);
        return info;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var item in _outbound.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                PipeMessageChannel? channel;
                lock (_sync) channel = _channel;
                if (channel?.IsConnected != true) continue;
                try { await channel.SendEventAsync(item.Type, item.Payload, item.Sequence, _stop.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    if (_stop.IsCancellationRequested) return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void TouchProgressLocked() => Interlocked.Exchange(ref _lastProgressTimestamp, DateTimeOffset.UtcNow.UtcTicks);

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _channel = null;
            _outbound.Writer.TryComplete();
            _stop.Cancel();
        }
        try { await _sendTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
