using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Storage.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFlowStudio.Storage.Tests;

public sealed class ProjectStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"GameFlowStudio.Tests.{Guid.NewGuid():N}");
    private readonly ProjectStorageService _storage = new(NullLogger<ProjectStorageService>.Instance);

    [Fact]
    public async Task SaveAndLoadPreservesNodesEdgesAndPositions()
    {
        var path = Path.Combine(_directory, "roundtrip.gflow"); var project = Project();
        await _storage.SaveAsync(project, path, CancellationToken.None); var loaded = await _storage.LoadAsync(path, CancellationToken.None);
        Assert.Equal(project.Scripts[0].Nodes.Count, loaded.Scripts[0].Nodes.Count); Assert.Equal(project.Scripts[0].Edges.Count, loaded.Scripts[0].Edges.Count); Assert.Equal(123.5, loaded.Scripts[0].Nodes[0].Position.X); Assert.Equal("start", loaded.Scripts[0].Edges[0].Source);
    }

    [Fact]
    public async Task SaveAndLoadPreservesScriptProperties()
    {
        var path = Path.Combine(_directory, "script-properties.gflow");
        var project = Project();
        project.Scripts[0].Name = "异常恢复脚本";
        project.Scripts[0].WindowScope = new() { Enabled = true, TitlePattern = "^Game", OutputVariable = "targets", ExecutionMode = WindowExecutionMode.WindowPriority };
        project.Scripts[0].DefaultNodeDelayMs = 75;

        await _storage.SaveAsync(project, path, CancellationToken.None);
        var loaded = await _storage.LoadAsync(path, CancellationToken.None);

        Assert.Equal("异常恢复脚本", loaded.Scripts[0].Name);
        Assert.True(loaded.Scripts[0].WindowScope.Enabled);
        Assert.Equal("^Game", loaded.Scripts[0].WindowScope.TitlePattern);
        Assert.Equal("targets", loaded.Scripts[0].WindowScope.OutputVariable);
        Assert.Equal(WindowExecutionMode.WindowPriority, loaded.Scripts[0].WindowScope.ExecutionMode);
        Assert.Equal(75, loaded.Scripts[0].DefaultNodeDelayMs);
    }

    [Fact]
    public async Task InvalidJsonReturnsClearError()
    {
        Directory.CreateDirectory(_directory); var path = Path.Combine(_directory,"invalid.gflow"); await File.WriteAllTextAsync(path,"{ invalid json");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _storage.LoadAsync(path,CancellationToken.None)); Assert.Contains("JSON 无效", error.Message);
    }

    [Fact]
    public async Task ExistingFileCanBeAtomicallyReplaced()
    {
        var path = Path.Combine(_directory,"replace.gflow"); var project = Project(); await _storage.SaveAsync(project,path,CancellationToken.None);
        project.Project.Name = "更新后"; await _storage.SaveAsync(project,path,CancellationToken.None); Assert.Equal("更新后",(await _storage.LoadAsync(path,CancellationToken.None)).Project.Name);
        Assert.Empty(Directory.GetFiles(_directory,"*.tmp"));
    }

    private static AutomationProject Project()
    {
        var start = new WorkflowNode { Id="start",Type=NodeTypes.Start,Position=new(){X=123.5,Y=45},Data=new(){DisplayName="开始"} }; var end = new WorkflowNode { Id="end",Type=NodeTypes.End,Position=new(){X=500,Y=45},Data=new(){DisplayName="结束"} };
        return new() { Project=new(){Id="storage",Name="存储测试"}, Scripts=[new(){Id="script-storage",Name="主流程",Nodes=[start,end],Edges=[new(){Id="edge",Source="start",Target="end"}]}] };
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory,true); }
}
