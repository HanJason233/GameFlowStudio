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
        var tracker = new TrackingLogExecutor();
        var project = Chain(Log("one"), Log("two"));
        await Runner(tracker).RunAsync(project, CancellationToken.None);
        Assert.Equal(["one", "two"], tracker.Executed);
    }

    [Fact]
    public async Task DisabledActionNodeIsSkipped()
    {
        var tracker = new TrackingLogExecutor();
        var disabled = Log("skip"); disabled.Data.Enabled = false;
        await Runner(tracker).RunAsync(Chain(disabled), CancellationToken.None);
        Assert.Empty(tracker.Executed);
    }

    [Fact]
    public async Task PausePreventsNextNodeUntilResume()
    {
        var executor = new GatedLogExecutor(); var runner = Runner(executor); var run = runner.RunAsync(Chain(Log("one"), Log("two")), CancellationToken.None);
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
        var executor = new BlockingLogExecutor(); var runner = Runner(executor); var run = runner.RunAsync(Chain(Log("block")), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); await runner.StopAsync(); await run;
        Assert.Equal(WorkflowExecutionState.Idle, runner.State);
    }

    [Fact]
    public async Task DuplicateRunIsRejected()
    {
        var executor = new BlockingLogExecutor(); var runner = Runner(executor); var first = runner.RunAsync(Chain(Log("block")), CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Chain(Log("other")), CancellationToken.None));
        await runner.StopAsync(); await first;
    }

    [Fact]
    public async Task ExecutorExceptionMarksRunnerFailedAndRaisesEvent()
    {
        var runner = Runner(new ThrowingLogExecutor()); string? error = null; runner.NodeFailed += (_, e) => error = e.Error;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Chain(Log("bad")), CancellationToken.None));
        Assert.Equal(WorkflowExecutionState.Failed, runner.State); Assert.Contains("测试异常", error);
    }

    [Fact]
    public async Task NodeEventsArePublishedInOrder()
    {
        var events = new List<string>(); var runner = Runner(new TrackingLogExecutor());
        runner.NodeStarted += (_, e) => events.Add($"start:{e.Node.Id}"); runner.NodeCompleted += (_, e) => events.Add($"done:{e.Node.Id}");
        await runner.RunAsync(Chain(Log("one")), CancellationToken.None);
        Assert.Equal(["start:start", "done:start", "start:one", "done:one", "start:end", "done:end"], events);
    }

    [Fact]
    public async Task DefaultDelayIsAppliedBetweenRegularNodes()
    {
        var runner = Runner(new TrackingLogExecutor());
        runner.DefaultNodeDelayMs = 30;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(Chain(Log("one"), Log("two")), CancellationToken.None);

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds >= 25, $"Expected default delay between regular nodes, elapsed {timer.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task WorkflowDefaultDelayOverridesRunnerDefault()
    {
        var runner = Runner(new TrackingLogExecutor());
        runner.DefaultNodeDelayMs = 0;
        var project = Chain(Log("one"), Log("two"));
        project.DefaultNodeDelayMs = 30;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(project, CancellationToken.None);

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds >= 25, $"Expected the workflow-specific delay to override the runner default, elapsed {timer.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task WorkflowWindowScopeInitializesContextBeforeNodes()
    {
        var executor = new WindowScopeTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Log("one"));
        project.WindowScope = new() { Enabled = true, TitlePattern = "^Game", OutputVariable = "targets" };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(new nint[] { 101, 202 }, executor.Handles);
        Assert.Equal(new long[] { 101, 202 }, Assert.IsType<long[]>(executor.OutputValue));
    }

    [Fact]
    public async Task ExplicitWaitTakesPrecedenceOverDefaultDelay()
    {
        var runner = Runner(new TrackingLogExecutor());
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

    private static WorkflowRunner Runner(INodeExecutor logExecutor, IWindowScopeService? windowScopes = null) => new(
        [new StartNodeExecutor(), new EndNodeExecutor(), new WaitNodeExecutor(), new MouseClickNodeExecutor(new NullInputAutomationService(), new NullCoordinateResolver()), logExecutor],
        new WorkflowValidator(), NullLogger<WorkflowRunner>.Instance, windowScopes);

    private static AutomationProject Chain(params WorkflowNode[] middle)
    {
        var project = NewProject(); var nodes = new[] { Node("start", NodeTypes.Start) }.Concat(middle).Concat([Node("end", NodeTypes.End)]).ToList(); project.Nodes = nodes;
        for (var index = 0; index < nodes.Count - 1; index++) project.Edges.Add(Edge(nodes[index], "output", nodes[index + 1]));
        return project;
    }

    internal static AutomationProject NewProject() => new() { Project = new() { Id = "test", Name = "test" } };
    internal static WorkflowNode Log(string id) => Node(id, NodeTypes.Log, ("message", id), ("level", "information"));
    internal static WorkflowNode Node(string id, string type, params (string Name, object Value)[] parameters) => new() { Id = id, Type = type, Position = new() { X = 1, Y = 1 }, Data = new() { DisplayName = id, Parameters = parameters.ToDictionary(p => p.Name, p => ProjectJson.Element(p.Value)) } };
    internal static WorkflowEdge Edge(WorkflowNode source, string handle, WorkflowNode target) => new() { Id = $"{source.Id}-{handle}-{target.Id}", Source = source.Id, SourceHandle = handle, Target = target.Id };

    private sealed class TrackingLogExecutor : NodeExecutorBase
    {
        public TrackingLogExecutor() : base(NodeTypes.Log) { }
        public List<string> Executed { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Executed.Add(node.Id); Started.TrySetResult(); return Task.FromResult(NodeExecutionResult.Ok()); }
    }
    private sealed class GatedLogExecutor : NodeExecutorBase
    {
        public GatedLogExecutor() : base(NodeTypes.Log) { }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Count { get; private set; }
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Count++; if (Count == 1) { FirstStarted.SetResult(); await ReleaseFirst.Task.WaitAsync(cancellationToken); } return NodeExecutionResult.Ok(); }
    }
    private sealed class BlockingLogExecutor : NodeExecutorBase
    {
        public BlockingLogExecutor() : base(NodeTypes.Log) { }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return NodeExecutionResult.Ok(); }
    }
    private sealed class ThrowingLogExecutor : NodeExecutorBase
    {
        public ThrowingLogExecutor() : base(NodeTypes.Log) { }
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("测试异常");
    }

    private sealed class WindowScopeTrackingExecutor : NodeExecutorBase
    {
        public WindowScopeTrackingExecutor() : base(NodeTypes.Log) { }
        public IReadOnlyList<nint> Handles { get; private set; } = [];
        public object? OutputValue { get; private set; }
        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
        {
            Handles = context.WindowHandles;
            OutputValue = context.Variables["targets"];
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

    private sealed class NullCoordinateResolver : IMouseClickCoordinateResolver
    {
        public (int X, int Y) Resolve(MouseClickParameters parameters, WorkflowExecutionContext context) => (parameters.X, parameters.Y);
    }
}
