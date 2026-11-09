using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Protocol;
using GameFlowStudio.Storage.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFlowStudio.Runtime.Tests;

public sealed class RuntimeProcessIntegrationTests
{
    [Fact]
    public async Task IndependentRuntimeLoadsValidatesAndExecutesAProjectOverNamedPipe()
    {
        Assert.True(OperatingSystem.IsWindows(), "The Runtime worker and named-pipe identity options require Windows.");
        var projectPath = Path.Combine(Path.GetTempPath(), "GameFlowStudio.Runtime.Tests", $"{Guid.NewGuid():N}.gflow");
        var pipeName = $"GameFlowStudio.Runtime.Tests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await new ProjectStorageService(NullLogger<ProjectStorageService>.Instance)
            .SaveAsync(CreateStartEndProject(), projectPath, CancellationToken.None);

        var workerPath = Environment.GetEnvironmentVariable("GAMEFLOWSTUDIO_RUNTIME_PATH");
        if (string.IsNullOrWhiteSpace(workerPath))
            workerPath = Path.Combine(AppContext.BaseDirectory, "GameFlowStudio.Runtime.exe");
        else
            workerPath = Path.GetFullPath(workerPath);
        Assert.True(File.Exists(workerPath), $"Runtime worker was not copied to test output: {workerPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        using var worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Runtime worker failed to start.");
        PipeMessageChannel? channel = null;

        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15000);
            channel = new PipeMessageChannel(pipe);
            var completed = new TaskCompletionSource<RuntimeEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waitStarted = new TaskCompletionSource<RuntimeEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? currentRunId = null;
            Action<ProtocolMessage> eventHandler = message =>
            {
                if (message.Kind == MessageKind.Event && message.Type is RuntimeEvents.RunCompleted or RuntimeEvents.NodeStarted)
                {
                    var payload = message.Payload.Deserialize<RuntimeEventPayload>(ProtocolJson.Options);
                    if (payload is null || payload.RunId != currentRunId) return;
                    if (message.Type == RuntimeEvents.RunCompleted) completed.TrySetResult(payload);
                    if (message.Type == RuntimeEvents.NodeStarted && payload.NodeId == "wait") waitStarted.TrySetResult(payload);
                }
            };
            channel.MessageReceived += eventHandler;
            channel.Start();

            var initialized = await channel.RequestAsync<InitializeResponse>(RuntimeCommands.Initialize,
                new InitializeRequest(token, ProtocolConstants.CurrentVersion, Environment.ProcessId),
                TimeSpan.FromSeconds(10));
            Assert.NotNull(initialized);
            Assert.True(initialized.Ready);
            Assert.NotEqual(Environment.ProcessId, initialized.RuntimeProcessId);

            var loaded = await channel.RequestAsync<LoadProjectResponse>(RuntimeCommands.LoadProject,
                new LoadProjectRequest(projectPath, "1"), TimeSpan.FromSeconds(10));
            Assert.NotNull(loaded);
            Assert.Single(loaded.Scripts);

            var validation = await channel.RequestAsync<ValidateResponse>(RuntimeCommands.Validate,
                new ValidateRequest("1"), TimeSpan.FromSeconds(10));
            Assert.NotNull(validation);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));

            var runId = currentRunId = Guid.NewGuid().ToString("N");
            var accepted = await channel.RequestAsync<RunAcceptedResponse>(RuntimeCommands.StartRun,
                new StartRunRequest(runId, "1"), TimeSpan.FromSeconds(10));
            Assert.NotNull(accepted);
            Assert.True(accepted.Accepted);
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var paused = await channel.RequestAsync<RuntimeStatusResponse>(RuntimeCommands.PauseRun, null, TimeSpan.FromSeconds(10));
            Assert.NotNull(paused);
            Assert.Equal(nameof(WorkflowExecutionState.Paused), paused.State);

            var runtimePid = initialized.RuntimeProcessId;
            await channel.DisposeAsync();
            channel = null;
            var reconnectPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await reconnectPipe.ConnectAsync(15000);
            channel = new PipeMessageChannel(reconnectPipe);
            channel.MessageReceived += eventHandler;
            channel.Start();
            var reinitialized = await channel.RequestAsync<InitializeResponse>(RuntimeCommands.Initialize,
                new InitializeRequest(token, ProtocolConstants.CurrentVersion, Environment.ProcessId),
                TimeSpan.FromSeconds(10));
            Assert.NotNull(reinitialized);
            Assert.Equal(runtimePid, reinitialized.RuntimeProcessId);
            var recovered = await channel.RequestAsync<RuntimeStatusResponse>(RuntimeCommands.GetStatus, null, TimeSpan.FromSeconds(10));
            Assert.NotNull(recovered);
            Assert.Equal(nameof(WorkflowExecutionState.Paused), recovered.State);

            var resumed = await channel.RequestAsync<RuntimeStatusResponse>(RuntimeCommands.ResumeRun, null, TimeSpan.FromSeconds(10));
            Assert.NotNull(resumed);
            Assert.Equal(nameof(WorkflowExecutionState.Running), resumed.State);
            var runCompleted = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(runId, runCompleted.RunId);

            var status = await channel.RequestAsync<RuntimeStatusResponse>(RuntimeCommands.GetStatus, null, TimeSpan.FromSeconds(10));
            Assert.NotNull(status);
            Assert.Equal(nameof(WorkflowExecutionState.Completed), status.State);

            currentRunId = Guid.NewGuid().ToString("N");
            waitStarted = new TaskCompletionSource<RuntimeEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAccepted = await channel.RequestAsync<RunAcceptedResponse>(RuntimeCommands.StartRun,
                new StartRunRequest(currentRunId, "1"), TimeSpan.FromSeconds(10));
            Assert.NotNull(secondAccepted);
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopping = await channel.RequestAsync<RuntimeStatusResponse>(RuntimeCommands.StopRun, null, TimeSpan.FromSeconds(10));
            Assert.NotNull(stopping);
            RuntimeStatusResponse? stopped = null;
            var stopDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < stopDeadline)
            {
                stopped = await channel.RequestAsync<RuntimeStatusResponse>(RuntimeCommands.GetStatus, null, TimeSpan.FromSeconds(10));
                if (stopped?.State == nameof(WorkflowExecutionState.Idle)) break;
                await Task.Delay(50);
            }
            Assert.NotNull(stopped);
            Assert.Equal(nameof(WorkflowExecutionState.Idle), stopped.State);

            _ = await channel.RequestAsync<JsonElement>(RuntimeCommands.Shutdown, null, TimeSpan.FromSeconds(10));
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, worker.ExitCode);
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync();
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            try { File.Delete(projectPath); } catch (IOException) { }
            try { Directory.Delete(Path.GetDirectoryName(projectPath)!, recursive: true); } catch (IOException) { }
        }
    }

    private static AutomationProject CreateStartEndProject()
    {
        var script = new AutomationScript { Id = "1", Name = "IPC integration run", DefaultNodeDelayMs = 0 };
        script.Nodes.Add(new WorkflowNode
        {
            Id = "start",
            Type = NodeTypes.Start,
            Data = new WorkflowNodeData { DisplayName = "Start" }
        });
        script.Nodes.Add(new WorkflowNode
        {
            Id = "wait",
            Type = NodeTypes.Wait,
            Data = new WorkflowNodeData
            {
                DisplayName = "Wait",
                Parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["durationMs"] = JsonSerializer.SerializeToElement(5000)
                }
            }
        });
        script.Nodes.Add(new WorkflowNode
        {
            Id = "end",
            Type = NodeTypes.End,
            Data = new WorkflowNodeData { DisplayName = "End" }
        });
        script.Edges.Add(new WorkflowEdge { Id = "start-wait", Source = "start", Target = "wait" });
        script.Edges.Add(new WorkflowEdge { Id = "wait-end", Source = "wait", Target = "end" });
        var project = new AutomationProject { Scripts = [script] };
        project.SetActiveScript(script);
        return project;
    }
}
