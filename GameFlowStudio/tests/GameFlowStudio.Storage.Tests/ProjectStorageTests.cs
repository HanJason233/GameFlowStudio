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

    [Theory]
    [InlineData(ScriptInteractionMode.Foreground, WindowExecutionMode.Serial)]
    [InlineData(ScriptInteractionMode.Foreground, WindowExecutionMode.PseudoParallel)]
    [InlineData(ScriptInteractionMode.Background, WindowExecutionMode.Parallel)]
    public async Task SaveAndLoadPreservesScriptProperties(
        ScriptInteractionMode interactionMode,
        WindowExecutionMode executionMode)
    {
        var path = Path.Combine(_directory, "script-properties.gflow");
        var project = Project();
        project.Scripts[0].Name = "异常恢复脚本";
        project.Scripts[0].WindowScope = new() { Enabled = true, TitlePattern = "^Game", ExecutionMode = executionMode };
        project.Scripts[0].InteractionMode = interactionMode;
        project.Scripts[0].DefaultNodeDelayMs = 75;

        await _storage.SaveAsync(project, path, CancellationToken.None);
        var loaded = await _storage.LoadAsync(path, CancellationToken.None);

        Assert.Equal("异常恢复脚本", loaded.Scripts[0].Name);
        Assert.True(loaded.Scripts[0].WindowScope.Enabled);
        Assert.Equal("^Game", loaded.Scripts[0].WindowScope.TitlePattern);
        Assert.Equal(executionMode, loaded.Scripts[0].WindowScope.ExecutionMode);
        Assert.Equal(interactionMode, loaded.Scripts[0].InteractionMode);
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

    [Fact]
    public async Task SuppliedSampleCanBeLoaded()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "basic-example.gflow");

        var project = await _storage.LoadAsync(path, CancellationToken.None);

        Assert.Single(project.Scripts);
    }

    [Theory]
    [InlineData("""{"project":{},"scripts":[]}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":"1.6","project":{},"scripts":[]}""", "1.6")]
    [InlineData("""{"schemaVersion":"1.8","project":{},"scripts":[]}""", "1.8")]
    [InlineData("""{"schemaVersion":"1.7","project":{}}""", "scripts")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":null}""", "scripts")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"interactionMode":"foreground","windowScope":{},"variables":{},"nodes":[],"edges":[]}]}""", "executionMode")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"windowScope":{"executionMode":"serial"},"variables":{},"nodes":[],"edges":[]}]}""", "interactionMode")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"interactionMode":"foreground","windowScope":{"executionMode":0},"variables":{},"nodes":[],"edges":[]}]}""", "executionMode")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"interactionMode":0,"windowScope":{"executionMode":"serial"},"variables":{},"nodes":[],"edges":[]}]}""", "interactionMode")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"interactionMode":"foreground","windowScope":{"executionMode":"serial"},"variables":{},"edges":[]}]}""", "nodes")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"interactionMode":"foreground","windowScope":{"executionMode":"serial"},"variables":{},"nodes":[{"position":{}}],"edges":[]}]}""", "data")]
    [InlineData("""{"schemaVersion":"1.7","project":{},"scripts":[{"id":null,"interactionMode":"foreground","windowScope":{"executionMode":"serial"},"variables":{},"nodes":[],"edges":[]}]}""", "id")]
    public async Task UnsupportedOrIncompleteDocumentsAreRejected(string json, string expectedMessage)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "unsupported.gflow");
        await File.WriteAllTextAsync(path, json);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.LoadAsync(path, CancellationToken.None));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownWindowExecutionModeIsRejected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "unknown-window-execution-mode.gflow");
        const string json = """
        {
          "schemaVersion": "1.7",
          "project": {},
          "scripts": [{
            "interactionMode": "foreground",
            "windowScope": { "executionMode": "unsupported" },
            "variables": {},
            "nodes": [],
            "edges": []
          }]
        }
        """;
        await File.WriteAllTextAsync(path, json);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.LoadAsync(path, CancellationToken.None));

        Assert.IsType<System.Text.Json.JsonException>(error.InnerException);
    }

    [Fact]
    public async Task NumericEnumValuesAreRejected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "numeric-enum.gflow");
        const string json = """
        {
          "schemaVersion": "1.7",
          "project": {},
          "scripts": [{
            "interactionMode": "foreground",
            "windowScope": { "executionMode": "serial" },
            "variables": { "count": { "type": 0, "value": 1 } },
            "nodes": [],
            "edges": []
          }]
        }
        """;
        await File.WriteAllTextAsync(path, json);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.LoadAsync(path, CancellationToken.None));

        Assert.IsType<System.Text.Json.JsonException>(error.InnerException);
    }

    [Fact]
    public async Task InvalidWindowExecutionModeCannotBeSaved()
    {
        var path = Path.Combine(_directory, "invalid-window-execution-mode.gflow");
        var project = Project();
        project.Scripts[0].WindowScope.ExecutionMode = (WindowExecutionMode)999;

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.SaveAsync(project, path, CancellationToken.None));

        Assert.Contains("executionMode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(ScriptInteractionMode.Foreground, WindowExecutionMode.Parallel)]
    [InlineData(ScriptInteractionMode.Background, WindowExecutionMode.Serial)]
    [InlineData(ScriptInteractionMode.Background, WindowExecutionMode.PseudoParallel)]
    public async Task InvalidInteractionAndExecutionModeCombinationCannotBeSaved(
        ScriptInteractionMode interactionMode,
        WindowExecutionMode executionMode)
    {
        var path = Path.Combine(_directory, "invalid-mode-combination.gflow");
        var project = Project();
        project.Scripts[0].InteractionMode = interactionMode;
        project.Scripts[0].WindowScope.ExecutionMode = executionMode;

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.SaveAsync(project, path, CancellationToken.None));

        Assert.Contains("组合无效", error.Message);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("foreground", "parallel")]
    [InlineData("background", "serial")]
    [InlineData("background", "pseudoParallel")]
    public async Task InvalidInteractionAndExecutionModeCombinationCannotBeLoaded(
        string interactionMode,
        string executionMode)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "invalid-mode-combination.gflow");
        var json = $$"""
        {
          "schemaVersion": "1.7",
          "project": {},
          "scripts": [{
            "interactionMode": "{{interactionMode}}",
            "windowScope": { "executionMode": "{{executionMode}}" },
            "variables": {},
            "nodes": [],
            "edges": []
          }]
        }
        """;
        await File.WriteAllTextAsync(path, json);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.LoadAsync(path, CancellationToken.None));

        Assert.Contains("组合无效", error.Message);
    }

    [Fact]
    public async Task FailedSaveDoesNotChangeTimestampAndRemovesTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "invalid-save.gflow");
        var project = Project();
        var originalTimestamp = project.Project.UpdatedAt;
        project.Scripts = null!;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _storage.SaveAsync(project, path, CancellationToken.None));

        Assert.Equal(originalTimestamp, project.Project.UpdatedAt);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveCreatesTimestampedSnapshotWithoutMutatingCaller()
    {
        var path = Path.Combine(_directory, "pure-save.gflow");
        var project = Project();
        var originalTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        project.Project.UpdatedAt = originalTimestamp;

        await _storage.SaveAsync(project, path, CancellationToken.None);
        var loaded = await _storage.LoadAsync(path, CancellationToken.None);

        Assert.Equal(originalTimestamp, project.Project.UpdatedAt);
        Assert.True(loaded.Project.UpdatedAt > originalTimestamp);
    }

    [Fact]
    public async Task ConcurrentSavesFromDifferentServiceInstancesRemainCoherent()
    {
        var path = Path.Combine(_directory, "concurrent.gflow");
        var projects = Enumerable.Range(0, 20)
            .Select(index =>
            {
                var project = Project();
                project.Project.Name = $"project-{index}";
                project.Scripts[0].Name = $"script-{index}";
                return project;
            })
            .ToArray();

        var saves = projects
            .Select(project => new ProjectStorageService(NullLogger<ProjectStorageService>.Instance)
                .SaveAsync(project, path, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(saves);

        var loaded = await _storage.LoadAsync(path, CancellationToken.None);
        var index = int.Parse(loaded.Project.Name["project-".Length..]);
        Assert.Equal($"script-{index}", loaded.Scripts[0].Name);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    private static AutomationProject Project()
    {
        var start = new WorkflowNode { Id="start",Type=NodeTypes.Start,Position=new(){X=123.5,Y=45},Data=new(){DisplayName="开始"} }; var end = new WorkflowNode { Id="end",Type=NodeTypes.End,Position=new(){X=500,Y=45},Data=new(){DisplayName="结束"} };
        return new() { Project=new(){Id="storage",Name="存储测试"}, Scripts=[new(){Id="script-storage",Name="主流程",Nodes=[start,end],Edges=[new(){Id="edge",Source="start",Target="end"}]}] };
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory,true); }
}
