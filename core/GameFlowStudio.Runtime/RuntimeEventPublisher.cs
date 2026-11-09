using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Protocol;

namespace GameFlowStudio.Runtime;

public sealed class RuntimeEventPublisher
{
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<string> _completedNodeIds = new(StringComparer.Ordinal);
    private PipeMessageChannel? _channel;
    private string? _runId;
    private long _sequence;
    private int _totalNodes;
    private int _completedNodes;

    public string? CurrentNodeId { get; private set; }
    public string? CurrentNodeName { get; private set; }

    public void Attach(PipeMessageChannel channel)
    {
        lock (_sync) _channel = channel;
    }

    public void Detach(PipeMessageChannel channel)
    {
        lock (_sync)
            if (ReferenceEquals(_channel, channel)) _channel = null;
    }

    public void BeginRun(string runId, AutomationProject project)
    {
        lock (_sync)
        {
            _runId = runId;
            _sequence = 0;
            _totalNodes = Math.Max(1, project.Scripts.Sum(script => script.Nodes.Count));
            _completedNodes = 0;
            _completedNodeIds.Clear();
            CurrentNodeId = null;
            CurrentNodeName = null;
        }
    }

    public void ObserveRunner(IWorkflowRunner runner)
    {
        runner.NodeStarted += OnNodeStarted;
        runner.NodeCompleted += OnNodeCompleted;
        runner.NodeFailed += OnNodeFailed;
        runner.StateChanged += OnStateChanged;
    }

    public Task PublishRunFailedAsync(string runId, string error) =>
        PublishAsync(RuntimeEvents.RunFailed, new RuntimeEventPayload { RunId = runId, Error = error });

    public Task PublishLogAsync(RuntimeLogInfo entry) =>
        PublishAsync(RuntimeEvents.LogReceived, new RuntimeEventPayload { Log = entry });

    public Task PublishAsync(string eventType, RuntimeEventPayload payload, CancellationToken cancellationToken = default) =>
        SendAsync(eventType, payload, cancellationToken);

    private void OnNodeStarted(object? sender, NodeExecutionEventArgs args)
    {
        lock (_sync)
        {
            CurrentNodeId = args.Node.Id;
            CurrentNodeName = args.Node.Data.DisplayName;
        }
        _ = SendAsync(RuntimeEvents.NodeStarted, new RuntimeEventPayload
        {
            NodeId = args.Node.Id,
            NodeName = args.Node.Data.DisplayName,
            WindowHandle = args.WindowHandle?.ToInt64(),
            WindowTitle = args.WindowTitle
        });
    }

    private void OnNodeCompleted(object? sender, NodeExecutionEventArgs args)
    {
        _ = SendAsync(RuntimeEvents.NodeCompleted, new RuntimeEventPayload
        {
            NodeId = args.Node.Id,
            NodeName = args.Node.Data.DisplayName,
            ElapsedMilliseconds = args.ElapsedMilliseconds,
            WindowHandle = args.WindowHandle?.ToInt64(),
            WindowTitle = args.WindowTitle
        });

        int completed;
        int total;
        lock (_sync)
        {
            if (_completedNodeIds.Add(args.Node.Id)) _completedNodes++;
            completed = _completedNodes;
            total = _totalNodes;
        }
        _ = SendAsync(RuntimeEvents.ProgressChanged, new RuntimeEventPayload
        {
            NodeId = args.Node.Id,
            CompletedNodes = completed,
            TotalNodes = total,
            ProgressPercent = Math.Clamp(completed * 100d / total, 0, 100)
        });
    }

    private void OnNodeFailed(object? sender, NodeExecutionEventArgs args) =>
        _ = SendAsync(RuntimeEvents.NodeFailed, new RuntimeEventPayload
        {
            NodeId = args.Node.Id,
            NodeName = args.Node.Data.DisplayName,
            Error = args.Error,
            ElapsedMilliseconds = args.ElapsedMilliseconds,
            WindowHandle = args.WindowHandle?.ToInt64(),
            WindowTitle = args.WindowTitle
        });

    private void OnStateChanged(object? sender, WorkflowStateChangedEventArgs args)
    {
        _ = SendAsync(RuntimeEvents.StateChanged, new RuntimeEventPayload
        {
            PreviousState = args.Previous.ToString(),
            State = args.Current.ToString()
        });
        if (args.Current == GameFlowStudio.Contracts.Enums.WorkflowExecutionState.Completed)
            _ = SendAsync(RuntimeEvents.RunCompleted, new RuntimeEventPayload { State = args.Current.ToString() });
    }

    private async Task SendAsync(string eventType, RuntimeEventPayload payload, CancellationToken cancellationToken = default)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PipeMessageChannel? channel;
            string? runId;
            long sequence;
            lock (_sync)
            {
                channel = _channel;
                runId = payload.RunId ?? _runId;
                sequence = ++_sequence;
            }
            if (channel is null || !channel.IsConnected) return;
            await channel.SendEventAsync(eventType, payload with { RunId = runId, Sequence = sequence }, sequence, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { _publishGate.Release(); }
    }
}
