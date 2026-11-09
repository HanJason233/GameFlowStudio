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
        var standaloneProjectPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "standalone.gflow");
        await new ProjectStorageService(NullLogger<ProjectStorageService>.Instance)
            .SaveAsync(CreateStartEndProject(0), standaloneProjectPath, CancellationToken.None);

        var workerPath = Environment.GetEnvironmentVariable("GAMEFLOWSTUDIO_RUNTIME_PATH");
        if (string.IsNullOrWhiteSpace(workerPath))
            workerPath = Path.Combine(AppContext.BaseDirectory, "GameFlowStudio.Runtime.exe");
        else
            workerPath = Path.GetFullPath(workerPath);
        Assert.True(File.Exists(workerPath), $"Runtime worker was not copied to test output: {workerPath}");
        using (var standalone = StartRuntime(workerPath, "--project", standaloneProjectPath, "--script", "1", "--run"))
        {
            var outputTask = standalone.StandardOutput.ReadToEndAsync();
            var errorTask = standalone.StandardError.ReadToEndAsync();
            await standalone.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(standalone.ExitCode == 0, $"Standalone exited with code {standalone.ExitCode}: {error}");
            Assert.Contains("Loaded", output);
            Assert.Contains("Completed", output);
        }
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
            await using (var badTokenPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await badTokenPipe.ConnectAsync(15000);
                await using var badTokenChannel = new PipeMessageChannel(badTokenPipe);
                badTokenChannel.Start();
                await Assert.ThrowsAsync<RemoteCommandException>(() => badTokenChannel.RequestAsync<InitializeResponse>(
                    RuntimeCommands.Initialize,
                    new InitializeRequest("wrong-token", ProtocolConstants.CurrentVersion, Environment.ProcessId),
                    TimeSpan.FromSeconds(5)));
            }

            await using (var badVersionPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await badVersionPipe.ConnectAsync(15000);
                await using var badVersionChannel = new PipeMessageChannel(badVersionPipe);
                badVersionChannel.Start();
                await AssertPipeRequestRejectedAsync(() => badVersionChannel.RequestAsync<InitializeResponse>(
                    RuntimeCommands.Initialize,
                    new InitializeRequest(token, "0.9", Environment.ProcessId),
                    TimeSpan.FromSeconds(5)));
            }

            await using (var unauthenticatedPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await unauthenticatedPipe.ConnectAsync(15000);
                await using var unauthenticatedChannel = new PipeMessageChannel(unauthenticatedPipe);
                unauthenticatedChannel.Start();
                await Assert.ThrowsAsync<RemoteCommandException>(() => unauthenticatedChannel.RequestAsync<RuntimeStatusResponse>(
                    RuntimeCommands.GetStatus, null, TimeSpan.FromSeconds(5)));
            }

            await using (var idlePipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await idlePipe.ConnectAsync(15000);
                var buffer = new byte[1];
                var read = await idlePipe.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(7));
                Assert.Equal(0, read);
            }

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

            await using (var reconnectWithoutAuthPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await reconnectWithoutAuthPipe.ConnectAsync(15000);
                await using var reconnectWithoutAuth = new PipeMessageChannel(reconnectWithoutAuthPipe);
                reconnectWithoutAuth.Start();
                await Assert.ThrowsAsync<RemoteCommandException>(() => reconnectWithoutAuth.RequestAsync<RuntimeStatusResponse>(
                    RuntimeCommands.GetStatus, null, TimeSpan.FromSeconds(5)));
            }

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
            var beforeDisconnect = await channel.RequestAsync<RuntimeRunSnapshot>(RuntimeCommands.GetRunSnapshot, null, TimeSpan.FromSeconds(10));
            Assert.NotNull(beforeDisconnect);
            var sequenceBeforeRunFinishes = beforeDisconnect.Sequence;
            await channel.DisposeAsync();
            channel = null;
            await Task.Delay(TimeSpan.FromMilliseconds(6500));

            var completedPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await completedPipe.ConnectAsync(15000);
            channel = new PipeMessageChannel(completedPipe);
            channel.MessageReceived += eventHandler;
            channel.Start();
            _ = await channel.RequestAsync<InitializeResponse>(RuntimeCommands.Initialize,
                new InitializeRequest(token, ProtocolConstants.CurrentVersion, Environment.ProcessId), TimeSpan.FromSeconds(10));
            RuntimeRunSnapshot? recoveredSnapshot = null;
            RuntimeEventsResponse? missedEvents = null;
            var recoveryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < recoveryDeadline)
            {
                recoveredSnapshot = await channel.RequestAsync<RuntimeRunSnapshot>(RuntimeCommands.GetRunSnapshot, null, TimeSpan.FromSeconds(10));
                if (recoveredSnapshot?.State == nameof(WorkflowExecutionState.Completed)) break;
                await Task.Delay(100);
            }
            Assert.NotNull(recoveredSnapshot);
            Assert.Equal(nameof(WorkflowExecutionState.Completed), recoveredSnapshot.State);
            Assert.Contains(recoveredSnapshot.Nodes, node => node.NodeId == "wait" && node.State == "Completed");
            Assert.NotEmpty(recoveredSnapshot.Logs);
            missedEvents = await channel.RequestAsync<RuntimeEventsResponse>(RuntimeCommands.GetEvents,
                new GetEventsRequest(sequenceBeforeRunFinishes), TimeSpan.FromSeconds(10));
            Assert.NotNull(missedEvents);
            Assert.False(missedEvents.HistoryTruncated);
            Assert.Contains(missedEvents.Events, item => item.Type == RuntimeEvents.NodeCompleted && item.Payload.RunId == runId);
            Assert.Contains(missedEvents.Events, item => item.Type == RuntimeEvents.RunCompleted && item.Payload.RunId == runId);
            Assert.All(missedEvents.Events.Zip(missedEvents.Events.Skip(1)), pair => Assert.Equal(pair.First.Sequence + 1, pair.Second.Sequence));

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

    private static Process StartRuntime(string workerPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Runtime process failed to start.");
    }

    private static async Task AssertPipeRequestRejectedAsync<T>(Func<Task<T?>> request)
    {
        try
        {
            _ = await request();
            Assert.Fail("The Runtime accepted a request that must be rejected.");
        }
        catch (Exception exception) when (exception is RemoteCommandException or IOException or TaskCanceledException)
        {
            // Unauthorized and incompatible pre-authentication clients are rejected and closed.
        }
    }

    private static AutomationProject CreateStartEndProject(int waitDurationMs = 5000)
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
                    ["durationMs"] = JsonSerializer.SerializeToElement(waitDurationMs)
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
