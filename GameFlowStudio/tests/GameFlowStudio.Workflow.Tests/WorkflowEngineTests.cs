using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Engine;
using GameFlowStudio.Workflow.Executors;
using GameFlowStudio.Workflow.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFlowStudio.Workflow.Tests;

public sealed class WorkflowEngineTests
{
    [Fact]
    public async Task SequentialNodesFollowEdges()
    {
        var tracker = new TrackingActionExecutor();
        var project = Chain(Action("one"), Action("two"));
        await Runner(tracker).RunAsync(project, CancellationToken.None);
        Assert.Equal(["one", "two"], tracker.Executed);
    }

    [Fact]
    public async Task PausePreventsNextNodeUntilResume()
    {
        var executor = new GatedActionExecutor(); var runner = Runner(executor); var run = runner.RunAsync(Chain(Action("one"), Action("two")), CancellationToken.None);
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runner.PauseAsync(); executor.ReleaseFirst.SetResult();
        await Task.Delay(80);
        Assert.Equal(1, executor.Count);
        await runner.ResumeAsync(); await run;
        Assert.Equal(2, executor.Count);
    }

    [Fact]
    public async Task StopCancelsWorkflow()
    {
        var executor = new BlockingActionExecutor(); var runner = Runner(executor); var run = runner.RunAsync(Chain(Action("block")), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); await runner.StopAsync(); await run;
        Assert.Equal(WorkflowExecutionState.Idle, runner.State);
    }

    [Fact]
    public async Task CompletedStateHandlerCanRestartWithoutOldRunClearingNewCancellation()
    {
        var executor = new RestartActionExecutor();
        var runner = Runner(executor);
        Task? restartedRun = null;
        runner.StateChanged += (_, args) =>
        {
            if (args.Current == WorkflowExecutionState.Completed && restartedRun is null)
                restartedRun = runner.RunAsync(Chain(Action("second")), CancellationToken.None);
        };

        await runner.RunAsync(Chain(Action("first")), CancellationToken.None);
        await executor.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(restartedRun);
        Assert.Equal(WorkflowExecutionState.Running, runner.State);
        await runner.StopAsync();
        await restartedRun!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WorkflowExecutionState.Idle, runner.State);
    }

    [Fact]
    public Task CompletionBoundaryPauseDoesNotLeaveRunnerPaused() =>
        AssertCompletionBoundaryAsync(stop: false);

    [Fact]
    public Task CompletionBoundaryStopDoesNotLeaveRunnerStopping() =>
        AssertCompletionBoundaryAsync(stop: true);

    [Fact]
    public async Task DuplicateRunIsRejected()
    {
        var executor = new BlockingActionExecutor(); var runner = Runner(executor); var first = runner.RunAsync(Chain(Action("block")), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Chain(Action("other")), CancellationToken.None));
        await runner.StopAsync(); await first;
    }

    [Fact]
    public async Task ExecutorExceptionMarksRunnerFailedAndRaisesEvent()
    {
        var runner = Runner(new ThrowingActionExecutor()); string? error = null; runner.NodeFailed += (_, e) => error = e.Error;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Chain(Action("bad")), CancellationToken.None));
        Assert.Equal(WorkflowExecutionState.Failed, runner.State); Assert.Contains("测试异常", error);
    }

    [Fact]
    public async Task NodeEventsArePublishedInOrder()
    {
        var events = new List<string>(); var runner = Runner(new TrackingActionExecutor());
        runner.NodeStarted += (_, e) => events.Add($"start:{e.Node.Id}"); runner.NodeCompleted += (_, e) => events.Add($"done:{e.Node.Id}");
        await runner.RunAsync(Chain(Action("one")), CancellationToken.None);
        Assert.Equal(["start:start", "done:start", "start:one", "done:one", "start:end", "done:end"], events);
    }

    [Fact]
    public async Task DefaultDelayIsAppliedBetweenRegularNodes()
    {
        var runner = Runner(new TrackingActionExecutor());
        runner.DefaultNodeDelayMs = 30;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(Chain(Action("one"), Action("two")), CancellationToken.None);

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds >= 25, $"Expected default delay between regular nodes, elapsed {timer.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task WorkflowDefaultDelayOverridesRunnerDefault()
    {
        var runner = Runner(new TrackingActionExecutor());
        runner.DefaultNodeDelayMs = 0;
        var project = Chain(Action("one"), Action("two"));
        project.DefaultNodeDelayMs = 30;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(project, CancellationToken.None);

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds >= 25, $"Expected the workflow-specific delay to override the runner default, elapsed {timer.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task JumpSwitchesToTargetScriptAction()
    {
        var tracker = new TrackingActionExecutor();
        var source = new AutomationScript { Id = "source", Name = "源脚本" };
        var target = new AutomationScript { Id = "target", Name = "目标脚本" };
        var start = Node("source-start", NodeTypes.Start);
        var jump = Node("jump", NodeTypes.Jump, ("scriptId", "target"), ("actionId", "target-action"));
        var sourceEnd = Node("source-end", NodeTypes.End);
        source.Nodes = [start, jump, sourceEnd];
        source.Edges = [Edge(start, "output", jump), Edge(jump, "output", sourceEnd)];
        var targetStart = Node("target-start", NodeTypes.Start);
        var targetAction = Action("target-action");
        var targetEnd = Node("target-end", NodeTypes.End);
        target.Nodes = [targetStart, targetAction, targetEnd];
        target.Edges = [Edge(targetStart, "output", targetAction), Edge(targetAction, "output", targetEnd)];
        var project = NewProject();
        project.Scripts = [source, target];
        project.ActiveScriptId = source.Id;
        project.Nodes = source.Nodes;
        project.Edges = source.Edges;

        await Runner(tracker).RunAsync(project, CancellationToken.None);

        Assert.Equal(["target-action"], tracker.Executed);
        Assert.Equal(target.Id, project.ActiveScriptId);
    }

    [Fact]
    public async Task CrossScriptJumpsReusePlanIndexesAndReportMissingTargetClearly()
    {
        var executor = new RepeatingJumpActionExecutor();
        var source = new AutomationScript { Id = "source", Name = "源脚本" };
        var sourceStart = Node("source-start", NodeTypes.Start);
        var sourceAction = Action("source-action");
        var sourceEnd = Node("source-end", NodeTypes.End);
        source.Nodes = [sourceStart, sourceAction, sourceEnd];
        source.Edges = [Edge(sourceStart, "output", sourceAction), Edge(sourceAction, "output", sourceEnd)];

        var target = new AutomationScript { Id = "target", Name = "目标脚本" };
        var targetStart = Node("target-start", NodeTypes.Start);
        var targetAction = Action("target-action");
        var targetEnd = Node("target-end", NodeTypes.End);
        target.Nodes = [targetStart, targetAction, targetEnd];
        target.Edges = [Edge(targetStart, "output", targetAction), Edge(targetAction, "output", targetEnd)];

        var project = NewProject();
        project.Scripts = [source, target];
        project.SetActiveScript(source);
        var runner = Runner(executor);
        runner.NodeCompleted += (_, args) =>
        {
            if (args.Node.Id == sourceAction.Id && target.Nodes.Count > 0)
            {
                target.Nodes.Clear();
                target.Edges.Clear();
            }
            else if (args.Node.Id == targetAction.Id && source.Nodes.Count > 0)
            {
                source.Nodes.Clear();
                source.Edges.Clear();
            }
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Equal(
            ["source-action", "target-action", "source-action", "target-action"],
            executor.Executed);
        Assert.Contains("跳转目标动作不存在", error.Message);
        Assert.Contains("missing-action", error.Message);
        Assert.Equal(WorkflowExecutionState.Failed, runner.State);
    }

    [Fact]
    public async Task WorkflowWindowScopeInitializesContextBeforeNodes()
    {
        var executor = new WindowScopeTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, TitlePattern = "^Game", ExecutionMode = WindowExecutionMode.FirstOnly };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(new nint[] { 101 }, executor.Handles);
        Assert.Equal(new long[] { 101 }, Assert.IsType<long[]>(executor.OutputValue));
    }

    [Fact]
    public async Task WindowPriorityRunsCompleteGraphPerWindow()
    {
        var executor = new WindowExecutionTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.WindowPriority };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(
            ["one:101", "two:101", "one:202", "two:202"],
            executor.Executions);
    }

    [Fact]
    public async Task ActionPriorityRunsEachWindowOneActionPerRound()
    {
        var executor = new WindowExecutionTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.ActionPriority };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(
            ["one:101", "one:202", "two:101", "two:202"],
            executor.Executions);
    }

    [Fact]
    public async Task ExplicitWaitTakesPrecedenceOverDefaultDelay()
    {
        var runner = Runner(new TrackingActionExecutor());
        runner.DefaultNodeDelayMs = 100;
        var project = NewProject();
        var start = Node("start", NodeTypes.Start);
        var wait = Node("wait", NodeTypes.Wait, ("durationMs", 20));
        var end = Node("end", NodeTypes.End);
        project.Nodes = [start, wait, end];
        project.Edges = [Edge(start, "output", wait), Edge(wait, "output", end)];
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(project, CancellationToken.None);

        timer.Stop();
        Assert.InRange(timer.ElapsedMilliseconds, 10, 90);
    }

    private static WorkflowRunner Runner(INodeExecutor actionExecutor, IWindowScopeService? windowScopes = null, INodeExecutor? endExecutor = null) => new(
        [new StartNodeExecutor(), endExecutor ?? new EndNodeExecutor(), new WaitNodeExecutor(), new JumpNodeExecutor(), new MouseClickNodeExecutor(new NullInputAutomationService(), new NullCoordinateResolver()), actionExecutor],
        new WorkflowValidator(), NullLogger<WorkflowRunner>.Instance, windowScopes);

    private static async Task AssertCompletionBoundaryAsync(bool stop)
    {
        var end = new GatedEndExecutor();
        var runner = Runner(new TrackingActionExecutor(), endExecutor: end);
        var run = runner.RunAsync(Chain(Action("one")), CancellationToken.None);
        await end.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var start = new ManualResetEventSlim(false);
        var release = Task.Run(() =>
        {
            start.Wait();
            end.Release.TrySetResult();
        });
        var control = Task.Run(async () =>
        {
            start.Wait();
            if (stop) await runner.StopAsync();
            else await runner.PauseAsync();
        });

        start.Set();
        await Task.WhenAll(release, control);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(WorkflowExecutionState.Paused, runner.State);
        Assert.NotEqual(WorkflowExecutionState.Stopping, runner.State);
        if (!stop) Assert.Equal(WorkflowExecutionState.Completed, runner.State);
        else Assert.True(runner.State is WorkflowExecutionState.Completed or WorkflowExecutionState.Idle);
    }

    private static AutomationProject Chain(params WorkflowNode[] middle)
    {
        var project = NewProject(); var nodes = new[] { Node("start", NodeTypes.Start) }.Concat(middle).Concat([Node("end", NodeTypes.End)]).ToList(); project.Nodes = nodes;
        for (var index = 0; index < nodes.Count - 1; index++) project.Edges.Add(Edge(nodes[index], "output", nodes[index + 1]));
        return project;
    }

    internal static AutomationProject NewProject() => new() { Project = new() { Id = "test", Name = "test" } };
    internal static WorkflowNode Action(string id) => Node(id, NodeTypes.TextInput, ("text", id));
    internal static WorkflowNode Node(string id, string type, params (string Name, object Value)[] parameters) => new() { Id = id, Type = type, Position = new() { X = 1, Y = 1 }, Data = new() { DisplayName = id, Parameters = parameters.ToDictionary(p => p.Name, p => ProjectJson.Element(p.Value)) } };
    internal static WorkflowEdge Edge(WorkflowNode source, string handle, WorkflowNode target) => new() { Id = $"{source.Id}-{handle}-{target.Id}", Source = source.Id, SourceHandle = handle, Target = target.Id };

    private sealed class TrackingActionExecutor : NodeExecutorBase
    {
        public TrackingActionExecutor() : base(NodeTypes.TextInput) { }
        public List<string> Executed { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Executed.Add(node.Id); Started.TrySetResult(); return Task.FromResult(NodeExecutionResult.Ok()); }
    }
    private sealed class GatedActionExecutor : NodeExecutorBase
    {
        public GatedActionExecutor() : base(NodeTypes.TextInput) { }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Count { get; private set; }
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Count++; if (Count == 1) { FirstStarted.SetResult(); await ReleaseFirst.Task.WaitAsync(cancellationToken); } return NodeExecutionResult.Ok(); }
    }
    private sealed class BlockingActionExecutor : NodeExecutorBase
    {
        public BlockingActionExecutor() : base(NodeTypes.TextInput) { }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return NodeExecutionResult.Ok(); }
    }
    private sealed class RestartActionExecutor : NodeExecutorBase
    {
        public RestartActionExecutor() : base(NodeTypes.TextInput) { }
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
        {
            if (node.Id != "second") return NodeExecutionResult.Ok();
            SecondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return NodeExecutionResult.Ok();
        }
    }
    private sealed class RepeatingJumpActionExecutor : NodeExecutorBase
    {
        private int _targetExecutions;

        public RepeatingJumpActionExecutor() : base(NodeTypes.TextInput) { }
        public List<string> Executed { get; } = [];

        public override Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            Executed.Add(node.Id);
            if (node.Id == "source-action")
                return Task.FromResult(NodeExecutionResult.Jump("target", "target-action"));

            _targetExecutions++;
            return Task.FromResult(_targetExecutions == 1
                ? NodeExecutionResult.Jump("source", "source-action")
                : NodeExecutionResult.Jump("target", "missing-action"));
        }
    }
    private sealed class GatedEndExecutor : NodeExecutorBase
    {
        public GatedEndExecutor() : base(NodeTypes.End) { }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return NodeExecutionResult.Ok();
        }
    }
    private sealed class ThrowingActionExecutor : NodeExecutorBase
    {
        public ThrowingActionExecutor() : base(NodeTypes.TextInput) { }
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("测试异常");
    }

    private sealed class WindowScopeTrackingExecutor : NodeExecutorBase
    {
        public WindowScopeTrackingExecutor() : base(NodeTypes.TextInput) { }
        public IReadOnlyList<nint> Handles { get; private set; } = [];
        public object? OutputValue { get; private set; }
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
        {
            Handles = context.WindowHandles;
            OutputValue = context.Variables["windowHandles"];
            return Task.FromResult(NodeExecutionResult.Ok());
        }
    }

    private sealed class WindowExecutionTrackingExecutor : NodeExecutorBase
    {
        public WindowExecutionTrackingExecutor() : base(NodeTypes.TextInput) { }
        public List<string> Executions { get; } = [];
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
        {
            Executions.Add($"{node.Id}:{context.WindowHandles.Single()}");
            return Task.FromResult(NodeExecutionResult.Ok());
        }
    }

    private sealed class FixedWindowScopeService(IReadOnlyList<nint> handles) : IWindowScopeService
    {
        public IReadOnlyList<nint> ResolveWindowHandles(string titlePattern) => handles;
    }

    private sealed class NullInputAutomationService : IInputAutomationService
    {
        public Task MoveMouseAsync(int x, int y, int durationMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClickAsync(int x, int y, MouseButtonType button, int clickCount, int intervalMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DragMouseAsync(int startX, int startY, int endX, int endY, MouseButtonType button, int durationMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScrollMouseAsync(int x, int y, int delta, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PressKeyAsync(string key, int durationMs, int repeatCount, int intervalMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TypeTextAsync(string text, int intervalMs, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullCoordinateResolver : IMouseCoordinateResolver
    {
        public (int X, int Y) Resolve(int x, int y, CoordinateMode mode, WorkflowExecutionContext context) => (x, y);
    }
}
