using System.Collections.Concurrent;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Engine;
using GameFlowStudio.Workflow.Executors;
using GameFlowStudio.Workflow.Recording;
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
    public async Task RecordedNodesDoNotReceiveTheCurrentScriptDefaultDelay()
    {
        var runner = Runner(new TrackingActionExecutor());
        runner.DefaultNodeDelayMs = 500;
        var existing = Action("existing");
        var boundary = Action("recording-start");
        var recorded = Action("recorded");
        boundary.Data.Parameters[RecordedScriptFactory.RecordingSessionParameter] = ProjectJson.Element("session");
        boundary.Data.Parameters[RecordedScriptFactory.RecordingBoundaryParameter] = ProjectJson.Element("start");
        recorded.Data.Parameters[RecordedScriptFactory.RecordingSessionParameter] = ProjectJson.Element("session");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(Chain(existing, boundary, recorded), CancellationToken.None);

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds < 250, $"Recorded nodes unexpectedly received the default delay; elapsed {timer.ElapsedMilliseconds} ms.");
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
        Assert.Equal(source.Id, project.ActiveScriptId);
        Assert.Same(source.Nodes, project.Nodes);
        Assert.Same(source.Edges, project.Edges);
    }

    [Fact]
    public async Task CrossScriptJumpKeepsCompatibleBackgroundMode()
    {
        var windows = new RecordingWindowAutomationService();
        var tracker = new ModeTrackingActionExecutor();
        var source = new AutomationScript
        {
            Id = "source",
            Name = "source",
            InteractionMode = ScriptInteractionMode.Background,
            DefaultNodeDelayMs = 0,
            WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel }
        };
        var sourceStart = Node("source-start", NodeTypes.Start);
        var jump = Node("jump", NodeTypes.Jump, ("scriptId", "target"), ("actionId", "target-action"));
        var sourceEnd = Node("source-end", NodeTypes.End);
        source.Nodes = [sourceStart, jump, sourceEnd];
        source.Edges = [Edge(sourceStart, "output", jump), Edge(jump, "output", sourceEnd)];

        var target = new AutomationScript
        {
            Id = "target",
            Name = "target",
            InteractionMode = ScriptInteractionMode.Background,
            DefaultNodeDelayMs = 0,
            WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel }
        };
        var targetStart = Node("target-start", NodeTypes.Start);
        var targetAction = Node(
            "target-action",
            NodeTypes.TextInput,
            ("text", "test"));
        var targetEnd = Node("target-end", NodeTypes.End);
        target.Nodes = [targetStart, targetAction, targetEnd];
        target.Edges = [Edge(targetStart, "output", targetAction), Edge(targetAction, "output", targetEnd)];

        var project = NewProject();
        project.Scripts = [source, target];
        project.SetActiveScript(source);

        await Runner(
            tracker,
            new FixedWindowScopeService([(nint)101]),
            windows: windows).RunAsync(project, CancellationToken.None);

        Assert.Equal([ScriptInteractionMode.Background], tracker.Modes);
        Assert.Empty(windows.Activations);
    }

    [Fact]
    public async Task BackgroundParallelCannotJumpToForegroundScript()
    {
        var source = new AutomationScript
        {
            Id = "source",
            Name = "source",
            InteractionMode = ScriptInteractionMode.Background,
            DefaultNodeDelayMs = 0,
            WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel }
        };
        var sourceStart = Node("source-start", NodeTypes.Start);
        var jump = Node("jump", NodeTypes.Jump, ("scriptId", "target"), ("actionId", "target-action"));
        var sourceEnd = Node("source-end", NodeTypes.End);
        source.Nodes = [sourceStart, jump, sourceEnd];
        source.Edges = [Edge(sourceStart, "output", jump), Edge(jump, "output", sourceEnd)];

        var target = new AutomationScript
        {
            Id = "target",
            Name = "target",
            InteractionMode = ScriptInteractionMode.Foreground,
            DefaultNodeDelayMs = 0,
            WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Serial }
        };
        var targetStart = Node("target-start", NodeTypes.Start);
        var targetAction = Action("target-action");
        var targetEnd = Node("target-end", NodeTypes.End);
        target.Nodes = [targetStart, targetAction, targetEnd];
        target.Edges = [Edge(targetStart, "output", targetAction), Edge(targetAction, "output", targetEnd)];

        var project = NewProject();
        project.Scripts = [source, target];
        project.SetActiveScript(source);
        var runner = Runner(
            new TrackingActionExecutor(),
            new FixedWindowScopeService([(nint)101, (nint)202]),
            validator: new AcceptingWorkflowValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Contains("前台/后台模式不同", error.Message);
        Assert.Equal(source.Id, project.ActiveScriptId);
    }

    [Theory]
    [InlineData(ScriptInteractionMode.Foreground, WindowExecutionMode.Parallel)]
    [InlineData(ScriptInteractionMode.Background, WindowExecutionMode.Serial)]
    [InlineData(ScriptInteractionMode.Background, WindowExecutionMode.PseudoParallel)]
    public async Task RunnerRejectsInvalidInteractionModeAndOperationFlowWithoutValidatorHelp(
        ScriptInteractionMode interactionMode,
        WindowExecutionMode executionMode)
    {
        var project = Chain(Action("one"));
        project.InteractionMode = interactionMode;
        project.WindowScope = new() { ExecutionMode = executionMode };
        var runner = Runner(
            new TrackingActionExecutor(),
            validator: new AcceptingWorkflowValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Contains("入口脚本的运行方式与操作流程不匹配", error.Message);
        Assert.Equal(WorkflowExecutionState.Failed, runner.State);
    }

    [Fact]
    public async Task CrossScriptJumpRejectsDifferentWindowTitlePattern()
    {
        var project = CrossScriptProject(
            ScriptInteractionMode.Background,
            new() { Enabled = true, TitlePattern = "^Source$", ExecutionMode = WindowExecutionMode.Parallel },
            ScriptInteractionMode.Background,
            new() { Enabled = true, TitlePattern = "^Target$", ExecutionMode = WindowExecutionMode.Parallel });
        var executor = new TrackingActionExecutor();
        var runner = Runner(
            executor,
            new FixedWindowScopeService([(nint)101]),
            validator: new AcceptingWorkflowValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Contains("窗口标题规则不同", error.Message);
        Assert.Empty(executor.Executed);
    }

    [Fact]
    public async Task CrossScriptJumpRejectsDisabledTargetWindowScope()
    {
        var project = CrossScriptProject(
            ScriptInteractionMode.Background,
            new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel },
            ScriptInteractionMode.Background,
            new() { Enabled = false, ExecutionMode = WindowExecutionMode.Parallel });
        var runner = Runner(
            new TrackingActionExecutor(),
            new FixedWindowScopeService([(nint)101]),
            validator: new AcceptingWorkflowValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Contains("窗口作用域启用状态不同", error.Message);
    }

    [Fact]
    public async Task CrossScriptJumpRejectsDifferentOperationFlow()
    {
        var project = CrossScriptProject(
            ScriptInteractionMode.Foreground,
            new() { Enabled = true, ExecutionMode = WindowExecutionMode.Serial },
            ScriptInteractionMode.Foreground,
            new() { Enabled = true, ExecutionMode = WindowExecutionMode.PseudoParallel });
        var runner = Runner(
            new TrackingActionExecutor(),
            new FixedWindowScopeService([(nint)101]),
            validator: new AcceptingWorkflowValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Contains("操作流程不同", error.Message);
    }

    [Fact]
    public async Task CrossScriptJumpRejectsWindowThatNoLongerMatchesCompatibleScope()
    {
        var project = CrossScriptProject(
            ScriptInteractionMode.Background,
            new() { Enabled = true, TitlePattern = "^Game$", ExecutionMode = WindowExecutionMode.Parallel },
            ScriptInteractionMode.Background,
            new() { Enabled = true, TitlePattern = "^Game$", ExecutionMode = WindowExecutionMode.Parallel });
        var runner = Runner(
            new TrackingActionExecutor(),
            new ChangingWindowScopeService([(nint)101], [(nint)202]),
            validator: new AcceptingWorkflowValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Contains("当前目标窗口已不再匹配", error.Message);
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
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, TitlePattern = "^Game", ExecutionMode = WindowExecutionMode.Serial };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(new nint[] { 101 }, executor.Handles);
        Assert.Equal(new long[] { 101 }, Assert.IsType<long[]>(executor.OutputValue));
    }

    [Fact]
    public async Task SerialRunsCompleteGraphPerWindow()
    {
        var executor = new WindowExecutionTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Serial };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(
            ["one:101", "two:101", "one:202", "two:202"],
            executor.Executions);
    }

    [Fact]
    public async Task DefaultWindowExecutionModeRunsCompleteGraphPerWindow()
    {
        var executor = new WindowExecutionTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(
            ["one:101", "two:101", "one:202", "two:202"],
            executor.Executions);
    }

    [Fact]
    public async Task SerialWaitsBeforeSwitchingToAnotherWindow()
    {
        var executor = new WindowExecutionTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"));
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Serial };
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await runner.RunAsync(project, CancellationToken.None);

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds >= 900, $"窗口切换前应等待约 1 秒，实际耗时 {timer.ElapsedMilliseconds} ms。");
    }

    [Fact]
    public async Task PseudoParallelRunsEachWindowOneActionPerRound()
    {
        var executor = new WindowExecutionTrackingExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.PseudoParallel };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(
            ["one:101", "one:202", "two:101", "two:202"],
            executor.Executions);
    }

    [Fact]
    public async Task BackgroundParallelRunsAllWindowsConcurrently()
    {
        var executor = new ParallelBarrierActionExecutor(2);
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"));
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };

        var run = runner.RunAsync(project, CancellationToken.None);
        var overlapFailure = await Record.ExceptionAsync(
            () => executor.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        executor.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(overlapFailure);
        Assert.Equal([(nint)101, (nint)202], executor.Handles.Order());
        Assert.Equal(WorkflowExecutionState.Completed, runner.State);
    }

    [Fact]
    public async Task BackgroundParallelExecutesRunProgramNodeOnlyOnce()
    {
        var executor = new CountingGlobalNodeExecutor(NodeTypes.RunProgram);
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202, (nint)303]));
        var project = Chain(Node("run-once", NodeTypes.RunProgram, ("path", "test.exe")));
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(1, executor.Count);
    }

    [Fact]
    public async Task BackgroundParallelFailureCancelsSiblingAndPreservesFirstError()
    {
        var executor = new ParallelFailureActionExecutor();
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"));
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(project, CancellationToken.None));

        Assert.Equal("窗口 101 执行失败。", error.Message);
        await executor.SiblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WorkflowExecutionState.Failed, runner.State);
    }

    [Fact]
    public async Task PauseBlocksEveryParallelTrackBeforeItsNextNode()
    {
        var executor = new ParallelPauseActionExecutor(2);
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"), Action("two"));
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };
        var run = runner.RunAsync(project, CancellationToken.None);

        try
        {
            await executor.AllFirstNodesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            executor.ReleaseFirstNodes.TrySetResult();
            await runner.StopAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            throw;
        }

        await runner.PauseAsync();
        executor.ReleaseFirstNodes.TrySetResult();
        await Task.Delay(80);
        Assert.Equal(0, executor.SecondNodeCount);

        await runner.ResumeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, executor.SecondNodeCount);
    }

    [Fact]
    public async Task StopCancelsEveryParallelTrack()
    {
        var executor = new ParallelBlockingActionExecutor(2);
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101, (nint)202]));
        var project = Chain(Action("one"));
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };
        var run = runner.RunAsync(project, CancellationToken.None);

        await executor.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runner.StopAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executor.CancelledCount);
        Assert.Equal(WorkflowExecutionState.Idle, runner.State);
    }

    [Fact]
    public async Task ScopedActionsActivateTheTargetBeforeEachOperation()
    {
        var windows = new RecordingWindowAutomationService();
        var executor = new ActivationAwareActionExecutor(windows);
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101]), windows: windows);
        var project = Chain(Action("one"), Action("two"));
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Serial };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal(2, windows.Activations.Count);
        Assert.Equal([1, 2], executor.ActivationCounts);
    }

    [Fact]
    public async Task BackgroundClickDoesNotActivateTheScopedWindow()
    {
        var windows = new RecordingWindowAutomationService();
        var background = new RecordingBackgroundMouseClickService();
        var runner = Runner(
            new TrackingActionExecutor(),
            new FixedWindowScopeService([(nint)101]),
            windows: windows,
            backgroundClicks: background);
        var click = Node(
            "background-click",
            NodeTypes.MouseClick,
            ("coordinateMode", "relative"),
            ("x", 10),
            ("y", 20));
        var project = Chain(click);
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Empty(windows.Activations);
        Assert.Equal((nint)101, background.WindowHandle);
        Assert.Equal((10, 20), (background.X, background.Y));
    }

    [Fact]
    public async Task BackgroundKeyboardInputDoesNotActivateTheScopedWindow()
    {
        var windows = new RecordingWindowAutomationService();
        var executor = new ActivationAwareActionExecutor(windows);
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101]), windows: windows);
        var input = Node("background-input", NodeTypes.TextInput, ("text", "test"));
        var project = Chain(input);
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Empty(windows.Activations);
        Assert.Equal([0], executor.ActivationCounts);
    }

    [Fact]
    public async Task BackgroundDetectorReadDoesNotActivateTheScopedWindow()
    {
        var windows = new RecordingWindowAutomationService();
        var executor = new ActivationAwareActionExecutor(windows, NodeTypes.ColorCondition, "true");
        var runner = Runner(executor, new FixedWindowScopeService([(nint)101]), windows: windows);
        var project = NewProject();
        var start = Node("start", NodeTypes.Start);
        var condition = Node(
            "background-color",
            NodeTypes.ColorCondition,
            ("targetColor", "#FFFFFF"));
        var trueEnd = Node("true-end", NodeTypes.End);
        var falseEnd = Node("false-end", NodeTypes.End);
        project.Nodes = [start, condition, trueEnd, falseEnd];
        project.Edges =
        [
            Edge(start, "output", condition),
            Edge(condition, "true", trueEnd),
            Edge(condition, "false", falseEnd)
        ];
        project.InteractionMode = ScriptInteractionMode.Background;
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Parallel };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Empty(windows.Activations);
        Assert.Equal([0], executor.ActivationCounts);
    }

    [Fact]
    public async Task ForegroundClickStillActivatesTheScopedWindow()
    {
        var windows = new RecordingWindowAutomationService();
        var runner = Runner(
            new TrackingActionExecutor(),
            new FixedWindowScopeService([(nint)101]),
            windows: windows);
        var click = Node("foreground-click", NodeTypes.MouseClick, ("x", 10), ("y", 20));
        var project = Chain(click);
        project.WindowScope = new() { Enabled = true, ExecutionMode = WindowExecutionMode.Serial };

        await runner.RunAsync(project, CancellationToken.None);

        Assert.Equal([(nint)101], windows.Activations);
    }

    [Fact]
    public async Task ArrangeWindowsUsesScreenOriginAndVerticalOffset()
    {
        var windows = new RecordingWindowAutomationService();
        var node = Node("arrange", NodeTypes.ArrangeWindows,
            ("titlePattern", "^Game"), ("x", 100), ("y", 100), ("offsetY", 20));
        var context = new WorkflowExecutionContext
        {
            Workflow = NewProject(),
            WaitIfPausedAsync = _ => Task.CompletedTask,
            Log = _ => { }
        };

        var result = await new ArrangeWindowsNodeExecutor(
            windows,
            new FixedWindowScopeService([(nint)101, (nint)202, (nint)303]))
            .ExecuteAsync(node, context, CancellationToken.None);

        Assert.True(result.Success);
        var arrangement = Assert.Single(windows.Arrangements);
        Assert.Equal(new nint[] { 101, 202, 303 }, arrangement.Handles);
        Assert.Equal(100, arrangement.X);
        Assert.Equal(100, arrangement.Y);
        Assert.Equal(20, arrangement.OffsetY);
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

    private static WorkflowRunner Runner(
        INodeExecutor actionExecutor,
        IWindowScopeService? windowScopes = null,
        INodeExecutor? endExecutor = null,
        IWindowAutomationService? windows = null,
        IBackgroundMouseClickService? backgroundClicks = null,
        IWorkflowValidator? validator = null) => new(
        [new StartNodeExecutor(), endExecutor ?? new EndNodeExecutor(), new WaitNodeExecutor(), new JumpNodeExecutor(), new MouseClickNodeExecutor(new NullInputAutomationService(), new NullCoordinateResolver(), backgroundClicks), actionExecutor],
        validator ?? new WorkflowValidator(), NullLogger<WorkflowRunner>.Instance, windowScopes, windows);

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

    private static AutomationProject CrossScriptProject(
        ScriptInteractionMode sourceInteractionMode,
        ScriptWindowScope sourceWindowScope,
        ScriptInteractionMode targetInteractionMode,
        ScriptWindowScope targetWindowScope)
    {
        var source = new AutomationScript
        {
            Id = "source",
            Name = "source",
            InteractionMode = sourceInteractionMode,
            WindowScope = sourceWindowScope,
            DefaultNodeDelayMs = 0
        };
        var sourceStart = Node("source-start", NodeTypes.Start);
        var jump = Node("jump", NodeTypes.Jump, ("scriptId", "target"), ("actionId", "target-action"));
        var sourceEnd = Node("source-end", NodeTypes.End);
        source.Nodes = [sourceStart, jump, sourceEnd];
        source.Edges = [Edge(sourceStart, "output", jump), Edge(jump, "output", sourceEnd)];

        var target = new AutomationScript
        {
            Id = "target",
            Name = "target",
            InteractionMode = targetInteractionMode,
            WindowScope = targetWindowScope,
            DefaultNodeDelayMs = 0
        };
        var targetAction = Action("target-action");
        var targetEnd = Node("target-end", NodeTypes.End);
        target.Nodes = [Node("target-start", NodeTypes.Start), targetAction, targetEnd];
        target.Edges = [Edge(target.Nodes[0], "output", targetAction), Edge(targetAction, "output", targetEnd)];

        var project = NewProject();
        project.Scripts = [source, target];
        project.SetActiveScript(source);
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

    private sealed class ParallelBarrierActionExecutor(int expectedCount) : NodeExecutorBase(NodeTypes.TextInput)
    {
        private int _startedCount;
        private readonly ConcurrentDictionary<nint, byte> _handles = new();

        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IEnumerable<nint> Handles => _handles.Keys;

        public override async Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            _handles.TryAdd(context.WindowHandles.Single(), 0);
            if (Interlocked.Increment(ref _startedCount) == expectedCount)
                AllStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return NodeExecutionResult.Ok();
        }
    }

    private sealed class CountingGlobalNodeExecutor(string nodeType) : NodeExecutorBase(nodeType)
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public override async Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            await Task.Delay(50, cancellationToken);
            return NodeExecutionResult.Ok();
        }
    }

    private sealed class ParallelFailureActionExecutor : NodeExecutorBase
    {
        public ParallelFailureActionExecutor() : base(NodeTypes.TextInput) { }
        public TaskCompletionSource SiblingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SiblingCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (context.WindowHandles.Single() == (nint)101)
            {
                await SiblingStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("窗口 101 执行失败。");
            }

            SiblingStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SiblingCancelled.TrySetResult();
                throw;
            }
            return NodeExecutionResult.Ok();
        }
    }

    private sealed class ParallelPauseActionExecutor(int expectedCount) : NodeExecutorBase(NodeTypes.TextInput)
    {
        private int _firstNodeCount;
        private int _secondNodeCount;

        public TaskCompletionSource AllFirstNodesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstNodes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SecondNodeCount => Volatile.Read(ref _secondNodeCount);

        public override async Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (node.Id == "one")
            {
                if (Interlocked.Increment(ref _firstNodeCount) == expectedCount)
                    AllFirstNodesStarted.TrySetResult();
                await ReleaseFirstNodes.Task.WaitAsync(cancellationToken);
            }
            else if (node.Id == "two")
            {
                Interlocked.Increment(ref _secondNodeCount);
            }
            return NodeExecutionResult.Ok();
        }
    }

    private sealed class ParallelBlockingActionExecutor(int expectedCount) : NodeExecutorBase(NodeTypes.TextInput)
    {
        private int _startedCount;
        private int _cancelledCount;

        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelledCount => Volatile.Read(ref _cancelledCount);

        public override async Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _startedCount) == expectedCount)
                AllStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelledCount);
                throw;
            }
            return NodeExecutionResult.Ok();
        }
    }

    private sealed class BlockingActionExecutor : NodeExecutorBase
    {
        public BlockingActionExecutor() : base(NodeTypes.TextInput) { }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken) { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return NodeExecutionResult.Ok(); }
    }

    private sealed class ModeTrackingActionExecutor : NodeExecutorBase
    {
        public ModeTrackingActionExecutor() : base(NodeTypes.TextInput) { }
        public List<ScriptInteractionMode> Modes { get; } = [];

        public override Task<NodeExecutionResult> ExecuteAsync(
            WorkflowNode node,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            Modes.Add(context.ScriptInteractionMode);
            return Task.FromResult(NodeExecutionResult.Ok());
        }
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

    private sealed class ActivationAwareActionExecutor(
        RecordingWindowAutomationService windows,
        string nodeType = NodeTypes.TextInput,
        string nextHandle = "output") : NodeExecutorBase(nodeType)
    {
        public List<int> ActivationCounts { get; } = [];

        public override Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken)
        {
            ActivationCounts.Add(windows.Activations.Count);
            return Task.FromResult(NodeExecutionResult.Ok(nextHandle));
        }
    }

    private sealed class FixedWindowScopeService(IReadOnlyList<nint> handles) : IWindowScopeService
    {
        public IReadOnlyList<nint> ResolveWindowHandles(string titlePattern) => handles;
    }

    private sealed class ChangingWindowScopeService(
        IReadOnlyList<nint> initialHandles,
        IReadOnlyList<nint> laterHandles) : IWindowScopeService
    {
        private int _calls;

        public IReadOnlyList<nint> ResolveWindowHandles(string titlePattern) =>
            Interlocked.Increment(ref _calls) == 1 ? initialHandles : laterHandles;
    }

    private sealed class AcceptingWorkflowValidator : IWorkflowValidator
    {
        public WorkflowValidationResult Validate(AutomationProject workflow) => new();
    }

    private sealed class RecordingWindowAutomationService : IWindowAutomationService
    {
        public List<nint> Activations { get; } = [];
        public List<(IReadOnlyList<nint> Handles, int X, int Y, int OffsetY)> Arrangements { get; } = [];
        public IReadOnlyList<WindowInfo> GetOpenWindows() => [];
        public bool ActivateWindow(nint handle) { Activations.Add(handle); return true; }
        public bool CloseWindow(nint handle) => true;
        public bool MinimizeWindow(nint handle) => true;
        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int offsetY) { Arrangements.Add((handles, x, y, offsetY)); return true; }
        public bool SetWindowResolution(nint handle, int width, int height) => true;
        public WindowBounds GetWindowBounds(nint handle) => throw new NotSupportedException();
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

    private sealed class RecordingBackgroundMouseClickService : IBackgroundMouseClickService
    {
        public nint WindowHandle { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }

        public Task ClickAsync(
            nint windowHandle,
            int screenX,
            int screenY,
            MouseButtonType button,
            int clickCount,
            int intervalMs,
            CancellationToken cancellationToken)
        {
            WindowHandle = windowHandle;
            X = screenX;
            Y = screenY;
            return Task.CompletedTask;
        }
    }
}
