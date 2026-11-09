using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Protocol;
using GameFlowStudio.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFlowStudio.Runtime.Tests;

public sealed class RuntimeCommandDispatcherTests
{
    [Fact]
    public async Task ConcurrentStartRunRequestsAcceptOnlyOneActiveRun()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        var first = fixture.Dispatcher.DispatchAsync(Command(RuntimeCommands.StartRun, new StartRunRequest("run-a", "script-a")), CancellationToken.None, true);
        await fixture.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = fixture.Dispatcher.DispatchAsync(Command(RuntimeCommands.StartRun, new StartRunRequest("run-b", "script-a")), CancellationToken.None, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, fixture.Runner.RunCount);
        await fixture.StopAsync();
        _ = await first;
    }

    [Fact]
    public async Task RepeatingRunIdIsIdempotentWhileActiveAndAfterCompletion()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        var first = await fixture.Dispatcher.DispatchAsync(
            Command(RuntimeCommands.StartRun, new StartRunRequest("same-run", "script-a")), CancellationToken.None, true);
        await fixture.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await fixture.Dispatcher.DispatchAsync(
            Command(RuntimeCommands.StartRun, new StartRunRequest("same-run", "script-a")), CancellationToken.None, true);

        Assert.Equal(first, duplicate);
        Assert.Equal(1, fixture.Runner.RunCount);
        await fixture.StopAsync();

        var afterCompletion = await fixture.Dispatcher.DispatchAsync(
            Command(RuntimeCommands.StartRun, new StartRunRequest("same-run", "script-a")), CancellationToken.None, true);
        Assert.Equal(first, afterCompletion);
        Assert.Equal(1, fixture.Runner.RunCount);
    }

    [Fact]
    public async Task ProjectLoadReservationRejectsAConcurrentRunStart()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        fixture.Storage.BlockNextLoad();
        var load = fixture.Dispatcher.LoadProjectAsync(new LoadProjectRequest("second.gflow", "script-a"));
        await fixture.Storage.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.DispatchAsync(
            Command(RuntimeCommands.StartRun, new StartRunRequest("during-load", "script-a")), CancellationToken.None, true));
        fixture.Storage.AllowLoad.TrySetResult();
        await load;
        Assert.Equal(0, fixture.Runner.RunCount);
    }

    [Fact]
    public async Task RuntimeEventsUseVisitedScriptScopeAndCountRepeatedExecutions()
    {
        await using var events = new RuntimeEventPublisher();
        var runner = new FakeWorkflowRunner();
        events.ObserveRunner(runner);
        var project = CreateProject();
        events.BeginRun("run-1", project, "script-a");

        var repeatedNode = project.Scripts[0].Nodes[0];
        runner.EmitStarted("script-a", repeatedNode);
        runner.EmitCompleted("script-a", repeatedNode);
        runner.EmitCompleted("script-a", repeatedNode);
        var firstSnapshot = events.GetSnapshot([]);
        Assert.Equal(2, firstSnapshot.CompletedNodes);
        Assert.Equal(2, firstSnapshot.TotalNodes);
        Assert.Equal("run-1", firstSnapshot.RunId);

        var jumpedNode = project.Scripts[1].Nodes[0];
        runner.EmitStarted("script-b", jumpedNode);
        runner.EmitCompleted("script-b", jumpedNode);
        var snapshot = events.GetSnapshot([]);
        Assert.Equal(3, snapshot.CompletedNodes);
        Assert.Equal(82, snapshot.TotalNodes);
        Assert.Equal("script-b", snapshot.CurrentScriptId);
    }

    [Fact]
    public async Task EventHistoryIsBoundedSequencedAndKeepsRunIdAtCreation()
    {
        await using var events = new RuntimeEventPublisher();
        var project = CreateProject();
        events.BeginRun("run-before", project, "script-a");
        await events.PublishAsync("captured_run", new RuntimeEventPayload());
        var captured = Assert.Single(events.GetEvents(0).Events);
        events.BeginRun("run-after", project, "script-a");
        for (var index = 0; index < 4200; index++)
            await events.PublishAsync("history", new RuntimeEventPayload { NodeId = index.ToString() });

        var response = events.GetEvents(0);
        Assert.True(response.HistoryTruncated);
        Assert.Equal(4096, response.Events.Count);
        Assert.True(response.Events[0].Sequence > 1);
        Assert.True(events.GetEvents(response.LastSequence + 1).HistoryTruncated);
        Assert.Equal("run-before", captured.Payload.RunId);
        Assert.All(response.Events.Zip(response.Events.Skip(1)), pair => Assert.Equal(pair.First.Sequence + 1, pair.Second.Sequence));
        Assert.All(response.Events.Skip(1), item => Assert.Equal("run-after", item.Payload.RunId));
    }

    [Fact]
    public async Task StoppingRunDoesNotRestoreAStaleRunningNode()
    {
        await using var events = new RuntimeEventPublisher();
        var runner = new FakeWorkflowRunner();
        events.ObserveRunner(runner);
        var project = CreateProject();
        events.BeginRun("run-stopped", project, "script-a");
        runner.EmitStarted("script-a", project.Scripts[0].Nodes[0]);
        runner.EmitState(WorkflowExecutionState.Stopping);
        runner.EmitState(WorkflowExecutionState.Idle);

        var snapshot = events.GetSnapshot([]);
        Assert.Null(snapshot.CurrentNodeId);
        Assert.Equal("Stopped", Assert.Single(snapshot.Nodes).State);
    }

    [Fact]
    public async Task LogEventsAreJournaledWithTheRunIdActiveWhenCreated()
    {
        await using var events = new RuntimeEventPublisher();
        var project = CreateProject();
        events.BeginRun("log-run", project, "script-a");
        var entry = new RuntimeLogInfo(1, DateTimeOffset.UtcNow, "Information", "test", "run log");

        await events.PublishLogAsync(entry);

        var record = Assert.Single(events.GetEvents(0).Events);
        Assert.Equal(RuntimeEvents.LogReceived, record.Type);
        Assert.Equal("log-run", record.Payload.RunId);
        Assert.Equal("run log", record.Payload.Log?.Message);
    }

    private static ProtocolMessage Command<T>(string type, T payload) => ProtocolMessage.Command(type, payload);

    private sealed class DispatcherFixture(RuntimeCommandDispatcher dispatcher, FakeWorkflowRunner runner, InMemoryStorage storage, RuntimeEventPublisher events) : IAsyncDisposable
    {
        public RuntimeCommandDispatcher Dispatcher { get; } = dispatcher;
        public FakeWorkflowRunner Runner { get; } = runner;
        public InMemoryStorage Storage { get; } = storage;

        public async Task StopAsync()
        {
            _ = await Dispatcher.DispatchAsync(Command(RuntimeCommands.StopRun, new { }), CancellationToken.None, true);
        }

        public static async Task<DispatcherFixture> CreateAsync()
        {
            var project = CreateProject();
            var storage = new InMemoryStorage(project);
            var runner = new FakeWorkflowRunner();
            var events = new RuntimeEventPublisher();
            events.ObserveRunner(runner);
            var dispatcher = new RuntimeCommandDispatcher("test-token", runner, new AlwaysValidValidator(), storage,
                events, new RuntimeLogStore(), NullLogger<RuntimeCommandDispatcher>.Instance);
            await dispatcher.LoadProjectAsync(new LoadProjectRequest("first.gflow", "script-a"));
            return new DispatcherFixture(dispatcher, runner, storage, events);
        }

        public async ValueTask DisposeAsync() => await events.DisposeAsync();
    }

    private sealed class InMemoryStorage(AutomationProject project) : IProjectStorageService
    {
        private int _blockNextLoad;
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(AutomationProject value, string filePath, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<AutomationProject> LoadAsync(string filePath, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _blockNextLoad, 0) != 0)
            {
                LoadStarted.TrySetResult();
                await AllowLoad.Task.WaitAsync(cancellationToken);
            }
            return project;
        }

        public void BlockNextLoad() => Interlocked.Exchange(ref _blockNextLoad, 1);
    }

    private sealed class AlwaysValidValidator : IWorkflowValidator
    {
        public WorkflowValidationResult Validate(AutomationProject workflow) => new();
    }

    private sealed class FakeWorkflowRunner : IWorkflowRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)WorkflowExecutionState.Idle;
        private int _runCount;
        public WorkflowExecutionState State => (WorkflowExecutionState)Volatile.Read(ref _state);
        public int DefaultNodeDelayMs { get; set; }
        public int RunCount => Volatile.Read(ref _runCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<NodeExecutionEventArgs>? NodeStarted;
        public event EventHandler<NodeExecutionEventArgs>? NodeCompleted;
        public event EventHandler<NodeExecutionEventArgs>? NodeFailed;
        public event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;

        public async Task RunAsync(AutomationProject workflow, CancellationToken cancellationToken)
        {
            _ = workflow;
            Interlocked.Increment(ref _runCount);
            SetState(WorkflowExecutionState.Running);
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (State == WorkflowExecutionState.Stopping) SetState(WorkflowExecutionState.Idle);
            else SetState(WorkflowExecutionState.Completed);
        }

        public Task PauseAsync() { SetState(WorkflowExecutionState.Paused); return Task.CompletedTask; }
        public Task ResumeAsync() { SetState(WorkflowExecutionState.Running); return Task.CompletedTask; }
        public Task StopAsync()
        {
            if (State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused) SetState(WorkflowExecutionState.Stopping);
            _release.TrySetResult();
            return Task.CompletedTask;
        }

        public void EmitStarted(string scriptId, WorkflowNode node) => NodeStarted?.Invoke(this, new NodeExecutionEventArgs(node, scriptId: scriptId));
        public void EmitCompleted(string scriptId, WorkflowNode node) => NodeCompleted?.Invoke(this, new NodeExecutionEventArgs(node, elapsedMilliseconds: 1, scriptId: scriptId));
        public void EmitFailed(string scriptId, WorkflowNode node, string error) => NodeFailed?.Invoke(this, new NodeExecutionEventArgs(node, error: error, scriptId: scriptId));
        public void EmitState(WorkflowExecutionState state) => SetState(state);

        private void SetState(WorkflowExecutionState state)
        {
            var old = (WorkflowExecutionState)Interlocked.Exchange(ref _state, (int)state);
            if (old != state) StateChanged?.Invoke(this, new WorkflowStateChangedEventArgs(old, state));
        }
    }

    private static AutomationProject CreateProject()
    {
        var first = CreateScript("script-a", 2);
        var second = CreateScript("script-b", 80);
        var project = new AutomationProject { Scripts = [first, second] };
        project.SetActiveScript(first);
        return project;
    }

    private static AutomationScript CreateScript(string id, int nodeCount)
    {
        var script = new AutomationScript { Id = id, Name = id };
        for (var index = 0; index < nodeCount; index++)
            script.Nodes.Add(new WorkflowNode { Id = $"{id}-node-{index}", Type = NodeTypes.Wait, Data = new WorkflowNodeData { DisplayName = $"Node {index}" } });
        return script;
    }
}
