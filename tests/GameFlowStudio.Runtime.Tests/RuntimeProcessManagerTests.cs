using System.Diagnostics;
using System.Text.Json;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Protocol;
using GameFlowStudio.Storage.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFlowStudio.Runtime.Tests;

public sealed class RuntimeProcessManagerTests
{
    [Fact]
    public async Task UnexpectedWorkerExitRestartsOwnedWorkerAndReportsInterruptedRun()
    {
        Assert.True(OperatingSystem.IsWindows(), "Runtime process management is a Windows integration test.");
        var workerPath = Environment.GetEnvironmentVariable("GAMEFLOWSTUDIO_RUNTIME_PATH");
        if (string.IsNullOrWhiteSpace(workerPath)) workerPath = Path.Combine(AppContext.BaseDirectory, "GameFlowStudio.Runtime.exe");
        workerPath = Path.GetFullPath(workerPath);
        Assert.True(File.Exists(workerPath), $"Runtime worker was not copied to test output: {workerPath}");

        var projectDirectory = Path.Combine(Path.GetTempPath(), "GameFlowStudio.Runtime.Tests", Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(projectDirectory, "recovery.gflow");
        Directory.CreateDirectory(projectDirectory);
        await new ProjectStorageService(NullLogger<ProjectStorageService>.Instance)
            .SaveAsync(CreateProject(), projectPath, CancellationToken.None);

        var started = new TaskCompletionSource<RuntimeEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartedSnapshot = new TaskCompletionSource<RuntimeRunSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectRestartSnapshot = 0;
        var client = new RuntimeClient();
        await using var manager = new RuntimeProcessManager(client,
            NullLogger<RuntimeProcessManager>.Instance, workerPath);
        manager.RuntimeEventReceived += message =>
        {
            if (message.Type != RuntimeEvents.NodeStarted) return;
            var payload = message.Payload.Deserialize<RuntimeEventPayload>(ProtocolJson.Options);
            if (payload?.RunId == "manager-recovery-run" && payload.NodeId == "wait") started.TrySetResult(payload);
        };
        manager.RunSnapshotRecovered += snapshot =>
        {
            if (Volatile.Read(ref expectRestartSnapshot) == 1
                && snapshot.RunId is null
                && snapshot.State == "Idle")
                restartedSnapshot.TrySetResult(snapshot);
        };

        await manager.LoadProjectAsync(projectPath, "1");
        var firstPid = manager.RuntimeProcessId;
        Assert.NotNull(firstPid);
        var accepted = await manager.StartRunAsync("manager-recovery-run", "1");
        Assert.True(accepted.Accepted);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Write(ref expectRestartSnapshot, 1);
        using (var worker = Process.GetProcessById(firstPid.Value))
        {
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var snapshot = await restartedSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(snapshot.RunId);
        Assert.Equal("Idle", snapshot.State);
        Assert.NotEqual(firstPid, manager.RuntimeProcessId);
        var recovered = await manager.GetRunSnapshotAsync();
        Assert.Equal("Idle", recovered.State);

        var restartedPid = manager.RuntimeProcessId!.Value;
        await manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await HasExitedAsync(restartedPid));
    }

    private static AutomationProject CreateProject()
    {
        var script = new AutomationScript { Id = "1", Name = "Recovery test", DefaultNodeDelayMs = 0 };
        script.Nodes.Add(new WorkflowNode { Id = "start", Type = NodeTypes.Start, Data = new WorkflowNodeData { DisplayName = "Start" } });
        script.Nodes.Add(new WorkflowNode
        {
            Id = "wait",
            Type = NodeTypes.Wait,
            Data = new WorkflowNodeData
            {
                DisplayName = "Wait",
                Parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["durationMs"] = JsonSerializer.SerializeToElement(10000)
                }
            }
        });
        script.Nodes.Add(new WorkflowNode { Id = "end", Type = NodeTypes.End, Data = new WorkflowNodeData { DisplayName = "End" } });
        script.Edges.Add(new WorkflowEdge { Id = "start-wait", Source = "start", Target = "wait" });
        script.Edges.Add(new WorkflowEdge { Id = "wait-end", Source = "wait", Target = "end" });
        var project = new AutomationProject { Scripts = [script] };
        project.SetActiveScript(script);
        return project;
    }

    private static async Task<bool> HasExitedAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }
}
