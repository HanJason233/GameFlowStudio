using System.Diagnostics;
using System.Text.Json;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Synchronization;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Workflow.Engine;

/// <summary>负责单个脚本图执行。</summary>
public sealed class WorkflowRunner : IWorkflowRunner
{
    private readonly IReadOnlyDictionary<string, INodeExecutor> _executors;
    private readonly IWorkflowValidator _validator;
    private readonly ILogger<WorkflowRunner> _logger;
    private readonly IWindowScopeService? _windowScopes;
    private readonly IWindowAutomationService? _windows;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly AsyncManualResetEvent _pauseGate = new(true);
    private CancellationTokenSource? _runCancellation;
    private WorkflowExecutionState _state = WorkflowExecutionState.Idle;
    private int _defaultNodeDelayMs;

    public WorkflowRunner(IEnumerable<INodeExecutor> executors, IWorkflowValidator validator, ILogger<WorkflowRunner> logger, IWindowScopeService? windowScopes = null, IWindowAutomationService? windows = null)
    {
        _executors = executors.ToDictionary(e => e.NodeType, StringComparer.Ordinal);
        _validator = validator;
        _logger = logger;
        _windowScopes = windowScopes;
        _windows = windows;
    }

    public WorkflowExecutionState State => _state;
    public int DefaultNodeDelayMs { get => _defaultNodeDelayMs; set => _defaultNodeDelayMs = Math.Max(0, value); }
    public int MaxExecutedNodes { get; set; } = 10_000;
    public event EventHandler<NodeExecutionEventArgs>? NodeStarted;
    public event EventHandler<NodeExecutionEventArgs>? NodeCompleted;
    public event EventHandler<NodeExecutionEventArgs>? NodeFailed;
    public event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;

    public async Task RunAsync(AutomationProject workflow, CancellationToken cancellationToken)
    {
        var token = await PrepareExecutionAsync(workflow, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteGraphAsync(workflow, token).ConfigureAwait(false);
            ChangeState(WorkflowExecutionState.Completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ChangeState(WorkflowExecutionState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作流执行失败");
            ChangeState(WorkflowExecutionState.Failed);
            throw;
        }
        finally
        {
            ReleaseExecution();
        }
    }

    private async Task<CancellationToken> PrepareExecutionAsync(AutomationProject workflow, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotRunning();
            EnsureValid(workflow);
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pauseGate.Set();
            ChangeState(WorkflowExecutionState.Running);
            return _runCancellation.Token;
        }
        finally { _stateLock.Release(); }
    }


    private void EnsureNotRunning()
    {
        if (_state is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping)
            throw new InvalidOperationException("工作流已经在运行，不能重复启动。");
    }

    private void EnsureValid(AutomationProject workflow)
    {
        var validation = _validator.Validate(workflow);
        if (validation.IsValid) return;
        ChangeState(WorkflowExecutionState.Failed);
        throw new WorkflowValidationException(string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
    }

    private async Task ExecuteGraphAsync(AutomationProject workflow, CancellationToken token)
    {
        var initialScript = ResolveInitialScript(workflow);
        var handles = ResolveWindowHandles(initialScript.WindowScope);
        var budget = new ExecutionBudget();
        if (handles is null)
        {
            await ExecuteGraphForWindowAsync(workflow, initialScript, null, budget, token).ConfigureAwait(false);
            return;
        }

        switch (initialScript.WindowScope.ExecutionMode)
        {
            case WindowExecutionMode.WindowPriority:
                foreach (var handle in handles)
                    await ExecuteGraphForWindowAsync(workflow, initialScript, [handle], budget, token).ConfigureAwait(false);
                return;
            case WindowExecutionMode.ActionPriority:
                await ExecuteGraphByActionPriorityAsync(workflow, initialScript, handles, budget, token).ConfigureAwait(false);
                return;
            default:
                await ExecuteGraphForWindowAsync(workflow, initialScript, [handles[0]], budget, token).ConfigureAwait(false);
                return;
        }
    }

    private async Task ExecuteGraphForWindowAsync(AutomationProject workflow, AutomationScript initialScript, IReadOnlyList<nint>? windowHandles, ExecutionBudget budget, CancellationToken token)
    {
        var context = CreateContext(workflow, initialScript, windowHandles);
        var currentScript = initialScript;
        var nodes = currentScript.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var current = currentScript.Nodes.Single(n => n.Type == Contracts.Nodes.NodeTypes.Start);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await _pauseGate.WaitAsync(token).ConfigureAwait(false);
            EnsureExecutionLimit(++budget.Executed);
            var result = await ExecuteNodeAsync(workflow, current, context, token).ConfigureAwait(false);
            if (current.Type == Contracts.Nodes.NodeTypes.End) return;
            if (result.JumpTarget is not null)
            {
                var targetScript = ResolveTargetScript(workflow, result.JumpTarget);
                var targetNodes = targetScript.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
                var target = targetNodes[result.JumpTarget.ActionId];
                await WaitBetweenNodesAsync(workflow, currentScript, current, target, context, token).ConfigureAwait(false);
                ActivateScript(workflow, targetScript, context, CurrentWindowScope(context));
                currentScript = targetScript;
                nodes = targetNodes;
                current = target;
                continue;
            }
            var next = ResolveNextNode(currentScript, nodes, current, result.NextHandle);
            await WaitBetweenNodesAsync(workflow, currentScript, current, next, context, token).ConfigureAwait(false);
            current = next;
        }
    }

    private async Task ExecuteGraphByActionPriorityAsync(AutomationProject workflow, AutomationScript initialScript, IReadOnlyList<nint> handles, ExecutionBudget budget, CancellationToken token)
    {
        var tracks = handles.Select(handle => CreateWindowTrack(workflow, initialScript, handle)).ToList();
        while (tracks.Count > 0)
        {
            for (var index = 0; index < tracks.Count;)
            {
                var track = tracks[index];
                token.ThrowIfCancellationRequested();
                await _pauseGate.WaitAsync(token).ConfigureAwait(false);
                EnsureExecutionLimit(++budget.Executed);
                var result = await ExecuteNodeAsync(workflow, track.Current, track.Context, token).ConfigureAwait(false);
                if (track.Current.Type == Contracts.Nodes.NodeTypes.End)
                {
                    tracks.RemoveAt(index);
                    continue;
                }

                if (result.JumpTarget is not null)
                {
                    var targetScript = ResolveTargetScript(workflow, result.JumpTarget);
                    var targetNodes = targetScript.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
                    var target = targetNodes[result.JumpTarget.ActionId];
                    await WaitBetweenNodesAsync(workflow, track.Script, track.Current, target, track.Context, token).ConfigureAwait(false);
                    ActivateScript(workflow, targetScript, track.Context, CurrentWindowScope(track.Context));
                    track.Script = targetScript;
                    track.Nodes = targetNodes;
                    track.Current = target;
                }
                else
                {
                    var next = ResolveNextNode(track.Script, track.Nodes, track.Current, result.NextHandle);
                    await WaitBetweenNodesAsync(workflow, track.Script, track.Current, next, track.Context, token).ConfigureAwait(false);
                    track.Current = next;
                }

                index++;
            }
        }
    }

    private WindowExecutionTrack CreateWindowTrack(AutomationProject workflow, AutomationScript script, nint handle) =>
        new(script, script.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal), script.Nodes.Single(n => n.Type == Contracts.Nodes.NodeTypes.Start), CreateContext(workflow, script, [handle]));

    private IReadOnlyList<nint>? ResolveWindowHandles(ScriptWindowScope? scope)
    {
        if (scope is not { Enabled: true }) return null;
        if (_windowScopes is null) throw new InvalidOperationException("窗口作用域服务未注册。");
        var handles = _windowScopes.ResolveWindowHandles(string.IsNullOrWhiteSpace(scope.TitlePattern) ? ".*" : scope.TitlePattern);
        if (handles.Count == 0) throw new InvalidOperationException("窗口作用域没有找到匹配的窗口。");
        return handles;
    }

    private sealed class WindowExecutionTrack(AutomationScript script, IReadOnlyDictionary<string, WorkflowNode> nodes, WorkflowNode current, WorkflowExecutionContext context)
    {
        public AutomationScript Script { get; set; } = script;
        public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; set; } = nodes;
        public WorkflowNode Current { get; set; } = current;
        public WorkflowExecutionContext Context { get; } = context;
    }

    private sealed class ExecutionBudget
    {
        public int Executed { get; set; }
    }

    private static AutomationScript ResolveInitialScript(AutomationProject workflow)
    {
        if (workflow.ActiveScriptId is not null)
            return workflow.Scripts.First(script => script.Id == workflow.ActiveScriptId);

        return workflow.Scripts.FirstOrDefault(script => ReferenceEquals(script.Nodes, workflow.Nodes))
            ?? new AutomationScript
            {
                Id = "runtime",
                Name = workflow.Project.Name,
                Variables = workflow.Variables,
                Nodes = workflow.Nodes,
                Edges = workflow.Edges,
                WindowScope = workflow.WindowScope ?? new(),
                DefaultNodeDelayMs = workflow.DefaultNodeDelayMs ?? 0
            };
    }

    private static AutomationScript ResolveTargetScript(AutomationProject workflow, WorkflowJumpTarget target) =>
        workflow.Scripts.First(script => script.Id == target.ScriptId);

    private void ActivateScript(AutomationProject workflow, AutomationScript script, WorkflowExecutionContext context, IReadOnlyList<nint>? windowHandles = null)
    {
        workflow.ActiveScriptId = script.Id;
        workflow.Variables = script.Variables;
        workflow.Nodes = script.Nodes;
        workflow.Edges = script.Edges;
        workflow.WindowScope = script.WindowScope;
        workflow.DefaultNodeDelayMs = script.DefaultNodeDelayMs;
        context.Variables.Clear();
        foreach (var variable in script.Variables)
            context.Variables[variable.Key] = ConvertElement(variable.Value.Value);
        context.WindowHandles = [];
        InitializeWindowScope(script.WindowScope, context, windowHandles);
    }

    private async Task<NodeExecutionResult> ExecuteNodeAsync(AutomationProject workflow, WorkflowNode node, WorkflowExecutionContext context, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        Raise(NodeStarted, new(node));
        try
        {
            ActivateContextWindow(context);
            var result = await InvokeExecutorAsync(node, context, token).ConfigureAwait(false);
            timer.Stop();
            if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "节点执行失败。");
            Raise(NodeCompleted, new(node, timer.ElapsedMilliseconds));
            context.Log(Log(workflow, node, WorkflowLogLevel.Information, "节点执行成功。", timer.ElapsedMilliseconds));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            timer.Stop();
            Raise(NodeFailed, new(node, timer.ElapsedMilliseconds, ex.Message));
            context.Log(Log(workflow, node, WorkflowLogLevel.Error, ex.Message, timer.ElapsedMilliseconds, ex));
            throw;
        }
    }

    private void ActivateContextWindow(WorkflowExecutionContext context)
    {
        if (_windows is null || context.WindowHandles.Count != 1) return;
        if (!_windows.ActivateWindow(context.WindowHandles[0])) throw new InvalidOperationException("无法激活当前窗口作用域中的目标窗口。");
    }

    private static IReadOnlyList<nint>? CurrentWindowScope(WorkflowExecutionContext context) =>
        context.WindowHandles.Count == 1 ? context.WindowHandles : null;

    private async Task<NodeExecutionResult> InvokeExecutorAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken token)
    {
        if (!_executors.TryGetValue(node.Type, out var executor))
            throw new InvalidOperationException($"没有为节点类型 {node.Type} 注册执行器。");
        return await executor.ExecuteAsync(node, context, token).ConfigureAwait(false);
    }

    private static WorkflowNode ResolveNextNode(AutomationScript script, IReadOnlyDictionary<string, WorkflowNode> nodes, WorkflowNode current, string handle)
    {
        var edge = script.Edges.SingleOrDefault(e => e.Source == current.Id && e.SourceHandle == handle)
            ?? throw new InvalidOperationException($"节点 {current.Data.DisplayName} 的 {handle} 出口没有连接。");
        return nodes[edge.Target];
    }

    private void EnsureExecutionLimit(int executed)
    {
        if (executed > MaxExecutedNodes) throw new InvalidOperationException($"执行节点数超过安全上限 {MaxExecutedNodes}。");
    }

    private Task WaitBetweenNodesAsync(AutomationProject workflow, AutomationScript script, WorkflowNode current, WorkflowNode next, WorkflowExecutionContext context, CancellationToken token) =>
        WaitBetweenNodesAsync(GetDefaultNodeDelay(workflow, script), current, next, context, token);

    private static Task WaitBetweenNodesAsync(int delay, WorkflowNode current, WorkflowNode next, WorkflowExecutionContext context, CancellationToken token) =>
        ShouldApplyDefaultDelay(delay, current, next)
            ? PausableDelay.WaitAsync(delay, context.WaitIfPausedAsync, token)
            : Task.CompletedTask;

    private int GetDefaultNodeDelay(AutomationProject workflow, AutomationScript script) =>
        script.Id == "runtime" ? workflow.DefaultNodeDelayMs ?? DefaultNodeDelayMs : script.DefaultNodeDelayMs;

    private static bool ShouldApplyDefaultDelay(int delay, WorkflowNode current, WorkflowNode next) =>
        delay > 0
        && current.Type is not (Contracts.Nodes.NodeTypes.Start or Contracts.Nodes.NodeTypes.Wait)
        && next.Type is not (Contracts.Nodes.NodeTypes.End or Contracts.Nodes.NodeTypes.Wait);

    private void ReleaseExecution()
    {
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    public Task PauseAsync()
    {
        if (_state != WorkflowExecutionState.Running) return Task.CompletedTask;
        _pauseGate.Reset(); ChangeState(WorkflowExecutionState.Paused); return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (_state != WorkflowExecutionState.Paused) return Task.CompletedTask;
        _pauseGate.Set(); ChangeState(WorkflowExecutionState.Running); return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_state is not (WorkflowExecutionState.Running or WorkflowExecutionState.Paused)) return Task.CompletedTask;
        ChangeState(WorkflowExecutionState.Stopping); _pauseGate.Set(); _runCancellation?.Cancel(); return Task.CompletedTask;
    }

    private WorkflowExecutionContext CreateContext(AutomationProject workflow, AutomationScript script, IReadOnlyList<nint>? windowHandles = null)
    {
        var context = new WorkflowExecutionContext
        {
            Workflow = workflow,
            WaitIfPausedAsync = token => _pauseGate.WaitAsync(token),
            Log = entry => _logger.Log(ToMicrosoftLevel(entry.Level), "[{WorkflowId}/{NodeId}/{NodeName}] {Message} (耗时 {Elapsed} ms) {Exception}", entry.WorkflowId, entry.NodeId ?? "-", entry.NodeName ?? "-", entry.Message, entry.ElapsedMilliseconds, entry.Exception)
        };
        if (script.Id != "runtime")
        {
            workflow.ActiveScriptId = script.Id;
            workflow.Variables = script.Variables;
            workflow.Nodes = script.Nodes;
            workflow.Edges = script.Edges;
            workflow.WindowScope = script.WindowScope;
            workflow.DefaultNodeDelayMs = script.DefaultNodeDelayMs;
        }
        foreach (var variable in script.Variables)
            context.Variables[variable.Key] = ConvertElement(variable.Value.Value);
        InitializeWindowScope(script.WindowScope, context, windowHandles);
        return context;
    }

    private void InitializeWindowScope(ScriptWindowScope? scope, WorkflowExecutionContext context, IReadOnlyList<nint>? windowHandles = null)
    {
        if (scope is not { Enabled: true }) return;
        var handles = windowHandles ?? ResolveWindowHandles(scope);
        if (handles is null || handles.Count == 0) throw new InvalidOperationException("窗口作用域没有找到匹配的窗口。");
        if (_windows is not null && !_windows.ActivateWindow(handles[0])) throw new InvalidOperationException("无法激活窗口作用域中的目标窗口。");
        context.WindowHandles = handles;
        context.Variables["windowHandles"] = handles.Select(handle => handle.ToInt64()).ToArray();
    }

    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(), JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => element.Clone()
    };

    private static WorkflowLogEntry Log(AutomationProject workflow, WorkflowNode node, WorkflowLogLevel level, string message, long? elapsed = null, Exception? exception = null) =>
        new(DateTimeOffset.Now, level, workflow.Project.Id, node.Id, node.Data.DisplayName, message, exception?.ToString(), elapsed);

    private static LogLevel ToMicrosoftLevel(WorkflowLogLevel level) => level switch
    {
        WorkflowLogLevel.Debug => LogLevel.Debug, WorkflowLogLevel.Warning => LogLevel.Warning,
        WorkflowLogLevel.Error => LogLevel.Error, _ => LogLevel.Information
    };

    private void ChangeState(WorkflowExecutionState next)
    {
        var previous = _state; _state = next;
        if (previous != next) Raise(StateChanged, new(previous, next));
    }

    private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler is null) return;
        foreach (EventHandler<T> subscriber in handler.GetInvocationList())
            try { subscriber(this, args); } catch (Exception ex) { _logger.LogWarning(ex, "工作流事件订阅者发生异常"); }
    }
}
