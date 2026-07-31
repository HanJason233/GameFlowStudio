using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Executors;
using GameFlowStudio.Workflow.Recording;
using GameFlowStudio.Workflow.Synchronization;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Workflow.Engine;

/// <summary>负责单个脚本图执行。</summary>
public sealed class WorkflowRunner : IWorkflowRunner
{
    private const int WindowSwitchDelayMilliseconds = 1000;
    private readonly Dictionary<string, INodeExecutor> _executors;
    private readonly IWorkflowValidator _validator;
    private readonly ILogger<WorkflowRunner> _logger;
    private readonly IWindowScopeService? _windowScopes;
    private readonly IWindowAutomationService? _windows;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly AsyncManualResetEvent _pauseGate = new(true);
    private readonly object _stateEventSync = new();
    private readonly Queue<WorkflowStateChangedEventArgs> _pendingStateChanges = [];
    private RunLease? _activeRun;
    private long _nextRunGeneration;
    private int _stateValue = (int)WorkflowExecutionState.Idle;
    private bool _isPublishingStateChanges;
    private int _defaultNodeDelayMs;

    public WorkflowRunner(IEnumerable<INodeExecutor> executors, IWorkflowValidator validator, ILogger<WorkflowRunner> logger, IWindowScopeService? windowScopes = null, IWindowAutomationService? windows = null)
    {
        _executors = executors.ToDictionary(e => e.NodeType, StringComparer.Ordinal);
        _validator = validator;
        _logger = logger;
        _windowScopes = windowScopes;
        _windows = windows;
    }

    public WorkflowExecutionState State => (WorkflowExecutionState)Volatile.Read(ref _stateValue);
    public int DefaultNodeDelayMs { get => _defaultNodeDelayMs; set => _defaultNodeDelayMs = Math.Max(0, value); }
    public int MaxExecutedNodes { get; set; } = 10_000;
    public event EventHandler<NodeExecutionEventArgs>? NodeStarted;
    public event EventHandler<NodeExecutionEventArgs>? NodeCompleted;
    public event EventHandler<NodeExecutionEventArgs>? NodeFailed;
    public event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;

    public async Task RunAsync(AutomationProject workflow, CancellationToken cancellationToken)
    {
        var run = await PrepareExecutionAsync(workflow, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteGraphAsync(workflow, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            await CompleteExecutionAsync(run, WorkflowExecutionState.Idle).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            try { _logger.LogError(ex, "工作流执行失败"); }
            finally { await CompleteExecutionAsync(run, WorkflowExecutionState.Failed).ConfigureAwait(false); }
            throw;
        }

        await CompleteExecutionAsync(run, WorkflowExecutionState.Completed).ConfigureAwait(false);
    }

    private async Task<RunLease> PrepareExecutionAsync(AutomationProject workflow, CancellationToken cancellationToken)
    {
        RunLease? run = null;
        WorkflowValidationException? validationException = null;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotRunning();
            var validation = _validator.Validate(workflow);
            if (!validation.IsValid)
            {
                validationException = new WorkflowValidationException(
                    string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
                QueueStateChangeLocked(WorkflowExecutionState.Failed);
            }
            else
            {
                run = new RunLease(
                    ++_nextRunGeneration,
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                _activeRun = run;
                _pauseGate.Set();
                QueueStateChangeLocked(WorkflowExecutionState.Running);
            }
        }
        finally { _lifecycleGate.Release(); }

        DrainStateChanges();
        if (validationException is not null) throw validationException;
        return run!;
    }


    private void EnsureNotRunning()
    {
        var state = (WorkflowExecutionState)_stateValue;
        if (_activeRun is not null || state is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping)
            throw new InvalidOperationException("工作流已经在运行，不能重复启动。");
    }

    private async Task CompleteExecutionAsync(RunLease run, WorkflowExecutionState terminalState)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_activeRun, run) && _activeRun.Generation == run.Generation)
            {
                _activeRun = null;
                _pauseGate.Set();
                run.Release();
                QueueStateChangeLocked(terminalState);
            }
            else
            {
                run.Release();
            }
        }
        finally { _lifecycleGate.Release(); }

        DrainStateChanges();
    }

    private async Task ExecuteGraphAsync(AutomationProject workflow, CancellationToken token)
    {
        var initialScript = ResolveInitialScript(workflow);
        var plan = new WorkflowExecutionPlan(workflow, initialScript);
        if (!ScriptExecutionCompatibility.IsOperationFlowValid(
                initialScript.InteractionMode,
                initialScript.WindowScope.ExecutionMode))
            throw new InvalidOperationException(
                $"入口脚本的运行方式与操作流程不匹配：{initialScript.InteractionMode} / " +
                $"{initialScript.WindowScope.ExecutionMode}。");
        var handles = ResolveWindowHandles(initialScript.WindowScope);
        var budget = new ExecutionBudget();
        if (handles is null)
        {
            await ExecuteGraphForWindowAsync(workflow, plan, null, budget, token).ConfigureAwait(false);
            return;
        }

        switch (initialScript.WindowScope.ExecutionMode)
        {
            case WindowExecutionMode.Serial:
                for (var index = 0; index < handles.Count; index++)
                {
                    if (index > 0)
                        await WaitBetweenWindowsAsync(token).ConfigureAwait(false);

                    var handle = handles[index];
                    await ExecuteGraphForWindowAsync(workflow, plan, [handle], budget, token).ConfigureAwait(false);
                }
                return;
            case WindowExecutionMode.PseudoParallel:
                await ExecuteGraphPseudoParallelAsync(workflow, plan, handles, budget, token).ConfigureAwait(false);
                return;
            case WindowExecutionMode.Parallel:
                await ExecuteGraphInParallelAsync(workflow, plan, handles, budget, token).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"不支持的窗口执行方式：{initialScript.WindowScope.ExecutionMode}。");
        }
    }

    private async Task ExecuteGraphForWindowAsync(
        AutomationProject workflow,
        WorkflowExecutionPlan plan,
        IReadOnlyList<nint>? windowHandles,
        ExecutionBudget budget,
        CancellationToken token,
        ParallelGlobalNodeCoordinator? globalNodes = null)
    {
        var track = CreateWindowTrack(workflow, plan.InitialGraph, windowHandles, globalNodes);
        while (!await ExecuteTrackStepAsync(workflow, plan, track, budget, token).ConfigureAwait(false)) { }
    }

    private async Task ExecuteGraphPseudoParallelAsync(
        AutomationProject workflow,
        WorkflowExecutionPlan plan,
        IReadOnlyList<nint> handles,
        ExecutionBudget budget,
        CancellationToken token)
    {
        var tracks = handles
            .Select(handle => CreateWindowTrack(workflow, plan.InitialGraph, [handle]))
            .ToList();
        nint? lastWindowHandle = null;
        while (tracks.Count > 0)
        {
            for (var index = 0; index < tracks.Count;)
            {
                var track = tracks[index];
                if (track.WindowHandle is { } windowHandle)
                {
                    if (lastWindowHandle.HasValue && lastWindowHandle.Value != windowHandle)
                        await WaitBetweenWindowsAsync(token).ConfigureAwait(false);
                    lastWindowHandle = windowHandle;
                }

                if (await ExecuteTrackStepAsync(workflow, plan, track, budget, token).ConfigureAwait(false))
                {
                    tracks.RemoveAt(index);
                    continue;
                }
                index++;
            }
        }
    }

    private async Task ExecuteGraphInParallelAsync(
        AutomationProject workflow,
        WorkflowExecutionPlan plan,
        IReadOnlyList<nint> handles,
        ExecutionBudget budget,
        CancellationToken token)
    {
        using var parallelCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var globalNodes = new ParallelGlobalNodeCoordinator();
        ExceptionDispatchInfo? firstFailure = null;
        var tasks = handles
            .Select(handle => Task.Run(
                async () =>
                {
                    try
                    {
                        await ExecuteGraphForWindowAsync(
                            workflow,
                            plan,
                            [handle],
                            budget,
                            parallelCancellation.Token,
                            globalNodes: globalNodes).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (parallelCancellation.IsCancellationRequested)
                    {
                        // 外部停止或其他窗口失败时，由并行调度器统一决定最终状态。
                    }
                    catch (Exception exception)
                    {
                        var captured = ExceptionDispatchInfo.Capture(exception);
                        if (Interlocked.CompareExchange(ref firstFailure, captured, null) is null)
                        {
                            try { parallelCancellation.Cancel(); }
                            catch (Exception cancellationException)
                            {
                                _logger.LogWarning(cancellationException, "取消其他并行窗口任务时发生异常。");
                            }
                        }
                    }
                },
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        firstFailure?.Throw();
    }

    private async Task<bool> ExecuteTrackStepAsync(
        AutomationProject workflow,
        WorkflowExecutionPlan plan,
        WindowExecutionTrack track,
        ExecutionBudget budget,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _pauseGate.WaitAsync(token).ConfigureAwait(false);
        EnsureExecutionLimit(budget.Take());

        var current = track.Current;
        var result = track.GlobalNodes is not null && ParallelGlobalNodeCoordinator.IsCoordinated(current.Type)
            ? await track.GlobalNodes.ExecuteAsync(
                track.Graph.Script.Id,
                current,
                track.NextGlobalNodeOccurrence(track.Graph.Script.Id, current.Id),
                () => ExecuteNodeAsync(workflow, current, track.Context, token)).ConfigureAwait(false)
            : await ExecuteNodeAsync(workflow, current, track.Context, token).ConfigureAwait(false);
        if (current.Type == Contracts.Nodes.NodeTypes.End) return true;

        ScriptExecutionGraph nextGraph;
        WorkflowNode next;
        if (result.JumpTarget is not null)
        {
            (nextGraph, next) = plan.ResolveJump(result.JumpTarget);
            EnsureJumpCompatible(track, nextGraph.Script);
            await WaitBetweenNodesAsync(workflow, track.Graph.Script, current, next, track.Context, token).ConfigureAwait(false);
            ActivateScript(nextGraph.Script, track.Context, CurrentWindowScope(track.Context));
        }
        else
        {
            nextGraph = track.Graph;
            next = nextGraph.ResolveNext(current, result.NextHandle);
            await WaitBetweenNodesAsync(workflow, nextGraph.Script, current, next, track.Context, token).ConfigureAwait(false);
        }

        track.Graph = nextGraph;
        track.Current = next;
        return false;
    }

    private WindowExecutionTrack CreateWindowTrack(
        AutomationProject workflow,
        ScriptExecutionGraph graph,
        IReadOnlyList<nint>? windowHandles,
        ParallelGlobalNodeCoordinator? globalNodes = null) =>
        new(graph, graph.StartNode, CreateContext(workflow, graph.Script, windowHandles), globalNodes);

    private IReadOnlyList<nint>? ResolveWindowHandles(ScriptWindowScope? scope)
    {
        if (scope is not { Enabled: true }) return null;
        if (_windowScopes is null) throw new InvalidOperationException("窗口作用域服务未注册。");
        var handles = _windowScopes.ResolveWindowHandles(
            ScriptExecutionCompatibility.NormalizeMatchPattern(scope.MatchPattern),
            ScriptExecutionCompatibility.NormalizeExcludePattern(scope.ExcludePattern));
        if (handles.Count == 0) throw new InvalidOperationException("窗口作用域没有找到匹配的窗口。");
        return handles;
    }

    private void EnsureJumpCompatible(WindowExecutionTrack track, AutomationScript target)
    {
        if (!ScriptExecutionCompatibility.TryValidateJump(track.Graph.Script, target, out var compatibilityError))
            throw new InvalidOperationException(
                $"不能从脚本 {track.Graph.Script.Id} 跳转到脚本 {target.Id}：{compatibilityError}");

        if (!target.WindowScope.Enabled)
        {
            if (track.Context.WindowHandles.Count != 0)
                throw new InvalidOperationException(
                    $"不能从脚本 {track.Graph.Script.Id} 跳转到脚本 {target.Id}：" +
                    "当前轨道已绑定窗口，目标整屏脚本会丢失该窗口上下文。");
            return;
        }

        if (track.Context.WindowHandles.Count != 1)
            throw new InvalidOperationException(
                $"不能从脚本 {track.Graph.Script.Id} 跳转到脚本 {target.Id}：" +
                "窗口作用域跳转需要唯一的当前目标窗口。");
        if (_windowScopes is null)
            throw new InvalidOperationException("窗口作用域服务未注册，无法验证跨脚本跳转目标。");

        var currentHandle = track.Context.WindowHandles[0];
        var matchingHandles = _windowScopes.ResolveWindowHandles(
            ScriptExecutionCompatibility.NormalizeMatchPattern(target.WindowScope.MatchPattern),
            ScriptExecutionCompatibility.NormalizeExcludePattern(target.WindowScope.ExcludePattern));
        if (!matchingHandles.Contains(currentHandle))
            throw new InvalidOperationException(
                $"不能从脚本 {track.Graph.Script.Id} 跳转到脚本 {target.Id}：" +
                "当前目标窗口已不再符合目标脚本的窗口匹配与排除规则。");
    }

    private sealed class WindowExecutionTrack(
        ScriptExecutionGraph graph,
        WorkflowNode current,
        WorkflowExecutionContext context,
        ParallelGlobalNodeCoordinator? globalNodes)
    {
        private readonly Dictionary<(string ScriptId, string NodeId), int> _globalNodeOccurrences = [];

        public ScriptExecutionGraph Graph { get; set; } = graph;
        public WorkflowNode Current { get; set; } = current;
        public WorkflowExecutionContext Context { get; } = context;
        public nint? WindowHandle { get; } = context.WindowHandles.Count == 1 ? context.WindowHandles[0] : null;
        public ParallelGlobalNodeCoordinator? GlobalNodes { get; } = globalNodes;

        public int NextGlobalNodeOccurrence(string scriptId, string nodeId)
        {
            var key = (scriptId, nodeId);
            var occurrence = _globalNodeOccurrences.GetValueOrDefault(key) + 1;
            _globalNodeOccurrences[key] = occurrence;
            return occurrence;
        }
    }

    private sealed class ParallelGlobalNodeCoordinator
    {
        private readonly ConcurrentDictionary<(string ScriptId, string NodeId, int Occurrence), Lazy<Task<NodeExecutionResult>>> _executions = new();

        public static bool IsCoordinated(string nodeType) =>
            nodeType is NodeTypes.RunProgram or NodeTypes.ArrangeWindows;

        public Task<NodeExecutionResult> ExecuteAsync(
            string scriptId,
            WorkflowNode node,
            int occurrence,
            Func<Task<NodeExecutionResult>> execute)
        {
            return _executions.GetOrAdd(
                (scriptId, node.Id, occurrence),
                _ => new Lazy<Task<NodeExecutionResult>>(
                    execute,
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
    }

    private sealed class ExecutionBudget
    {
        private int _executed;

        public int Take() => Interlocked.Increment(ref _executed);
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
                DefaultNodeDelayMs = workflow.DefaultNodeDelayMs ?? 0,
                InteractionMode = workflow.InteractionMode
            };
    }

    private void ActivateScript(AutomationScript script, WorkflowExecutionContext context, IReadOnlyList<nint>? windowHandles = null)
    {
        context.Variables.Clear();
        foreach (var variable in script.Variables)
            context.Variables[variable.Key] = ConvertElement(variable.Value.Value);
        context.ScriptInteractionMode = script.InteractionMode;
        context.WindowHandles = [];
        InitializeWindowScope(script.WindowScope, context, windowHandles);
    }

    private async Task<NodeExecutionResult> ExecuteNodeAsync(AutomationProject workflow, WorkflowNode node, WorkflowExecutionContext context, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        Raise(NodeStarted, new(node));
        try
        {
            if (RequiresContextWindowActivation(node, context.ScriptInteractionMode)) ActivateContextWindow(context);
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

    private static bool UsesIndependentWindowTarget(string nodeType) => nodeType is
        NodeTypes.RunProgram or NodeTypes.CloseProgram or NodeTypes.MinimizeWindow or NodeTypes.ArrangeWindows or NodeTypes.SetWindowResolution;

    private static bool RequiresContextWindowActivation(
        WorkflowNode node,
        ScriptInteractionMode scriptInteractionMode)
    {
        if (UsesIndependentWindowTarget(node.Type)
            || node.Type is NodeTypes.Start or NodeTypes.End or NodeTypes.Wait or NodeTypes.Jump or NodeTypes.WindowScope)
            return false;

        return scriptInteractionMode != ScriptInteractionMode.Background
            || !SupportsBackgroundInteraction(node.Type);
    }

    private static bool SupportsBackgroundInteraction(string nodeType) => nodeType is
        NodeTypes.MouseClick or NodeTypes.MouseMove or NodeTypes.MouseDrag or NodeTypes.MouseScroll
        or NodeTypes.KeyPress or NodeTypes.TextInput
        or NodeTypes.ColorCondition or NodeTypes.ImageCondition or NodeTypes.OcrCondition;

    private static IReadOnlyList<nint>? CurrentWindowScope(WorkflowExecutionContext context) =>
        context.WindowHandles.Count == 1 ? context.WindowHandles : null;

    private async Task<NodeExecutionResult> InvokeExecutorAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken token)
    {
        if (!_executors.TryGetValue(node.Type, out var executor))
            throw new InvalidOperationException($"没有为节点类型 {node.Type} 注册执行器。");
        return await executor.ExecuteAsync(node, context, token).ConfigureAwait(false);
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

    private Task WaitBetweenWindowsAsync(CancellationToken token) =>
        PausableDelay.WaitAsync(WindowSwitchDelayMilliseconds, _pauseGate.WaitAsync, token);

    private static bool ShouldApplyDefaultDelay(int delay, WorkflowNode current, WorkflowNode next) =>
        delay > 0
        && current.Type is not (Contracts.Nodes.NodeTypes.Start or Contracts.Nodes.NodeTypes.Wait)
        && next.Type is not (Contracts.Nodes.NodeTypes.End or Contracts.Nodes.NodeTypes.Wait)
        && !IsRecordingBoundary(current)
        && !IsRecordingBoundary(next)
        && !SharesRecordingSession(current, next);

    private static bool IsRecordingBoundary(WorkflowNode node) =>
        node.Data.Parameters.TryGetValue(RecordedScriptFactory.RecordingBoundaryParameter, out var boundary)
        && boundary.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(boundary.GetString());

    private static bool SharesRecordingSession(WorkflowNode current, WorkflowNode next) =>
        TryGetRecordingSession(current, out var currentSession)
        && TryGetRecordingSession(next, out var nextSession)
        && string.Equals(currentSession, nextSession, StringComparison.Ordinal);

    private static bool TryGetRecordingSession(WorkflowNode node, out string? session)
    {
        session = null;
        if (!node.Data.Parameters.TryGetValue(
                RecordedScriptFactory.RecordingSessionParameter,
                out var value)
            || value.ValueKind != JsonValueKind.String)
            return false;
        session = value.GetString();
        return !string.IsNullOrWhiteSpace(session);
    }

    public async Task PauseAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeRun is not null && (WorkflowExecutionState)_stateValue == WorkflowExecutionState.Running)
            {
                _pauseGate.Reset();
                QueueStateChangeLocked(WorkflowExecutionState.Paused);
            }
        }
        finally { _lifecycleGate.Release(); }

        DrainStateChanges();
    }

    public async Task ResumeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeRun is not null && (WorkflowExecutionState)_stateValue == WorkflowExecutionState.Paused)
            {
                _pauseGate.Set();
                QueueStateChangeLocked(WorkflowExecutionState.Running);
            }
        }
        finally { _lifecycleGate.Release(); }

        DrainStateChanges();
    }

    public async Task StopAsync()
    {
        RunLease? run = null;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = (WorkflowExecutionState)_stateValue;
            if (_activeRun is not null && state is (WorkflowExecutionState.Running or WorkflowExecutionState.Paused))
            {
                run = _activeRun;
                run.Retain();
                _pauseGate.Set();
                QueueStateChangeLocked(WorkflowExecutionState.Stopping);
            }
        }
        finally { _lifecycleGate.Release(); }

        try
        {
            run?.CancelAndRelease();
        }
        finally
        {
            DrainStateChanges();
        }
    }

    private WorkflowExecutionContext CreateContext(AutomationProject workflow, AutomationScript script, IReadOnlyList<nint>? windowHandles = null)
    {
        var context = new WorkflowExecutionContext
        {
            Workflow = workflow,
            ScriptInteractionMode = script.InteractionMode,
            WaitIfPausedAsync = token => _pauseGate.WaitAsync(token),
            Log = WriteLogEntry
        };
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

    private static string FormatLogMessage(WorkflowLogEntry entry)
    {
        var message = $"[{entry.WorkflowId}/{entry.NodeId ?? "-"}/{entry.NodeName ?? "-"}] {entry.Message} (耗时 {entry.ElapsedMilliseconds?.ToString() ?? "-"} ms)";
        return string.IsNullOrWhiteSpace(entry.Exception) ? message : $"{message} {entry.Exception}";
    }

    private static LogLevel ToMicrosoftLevel(WorkflowLogLevel level) => level switch
    {
        WorkflowLogLevel.Debug => LogLevel.Debug, WorkflowLogLevel.Warning => LogLevel.Warning,
        WorkflowLogLevel.Error => LogLevel.Error, _ => LogLevel.Information
    };

    private void WriteLogEntry(WorkflowLogEntry entry)
    {
        var level = ToMicrosoftLevel(entry.Level);
        if (_logger.IsEnabled(level))
            _logger.Log(level, "{WorkflowEntry}", FormatLogMessage(entry));
    }

    private void QueueStateChangeLocked(WorkflowExecutionState next)
    {
        var previous = (WorkflowExecutionState)_stateValue;
        if (previous == next) return;
        Volatile.Write(ref _stateValue, (int)next);
        lock (_stateEventSync)
            _pendingStateChanges.Enqueue(new(previous, next));
    }

    private void DrainStateChanges()
    {
        lock (_stateEventSync)
        {
            if (_isPublishingStateChanges) return;
            _isPublishingStateChanges = true;
        }

        while (true)
        {
            WorkflowStateChangedEventArgs next;
            lock (_stateEventSync)
            {
                if (_pendingStateChanges.Count == 0)
                {
                    _isPublishingStateChanges = false;
                    return;
                }
                next = _pendingStateChanges.Dequeue();
            }
            Raise(StateChanged, next);
        }
    }

    private sealed class RunLease(long generation, CancellationTokenSource cancellation)
    {
        private int _references = 1;
        private int _disposed;

        public long Generation { get; } = generation;
        public CancellationToken Token { get; } = cancellation.Token;

        public void Retain() => Interlocked.Increment(ref _references);

        public void CancelAndRelease()
        {
            try { cancellation.Cancel(); }
            finally { Release(); }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            if (Interlocked.Exchange(ref _disposed, 1) == 0) cancellation.Dispose();
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler is null) return;
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
            try { subscriber(this, args); } catch (Exception ex) { _logger.LogWarning(ex, "工作流事件订阅者发生异常"); }
    }
}
