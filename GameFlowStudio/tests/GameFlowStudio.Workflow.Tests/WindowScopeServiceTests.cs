using GameFlowStudio.Automation.Windows;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Workflow.Tests;

public sealed class WindowScopeServiceTests
{
    [Fact]
    public void ResolveWindowHandlesReturnsAllRegexMatchesIgnoringCase()
    {
        var windows = new FakeWindowAutomationService(
        [
            new((nint)101, "Game Client - Alpha"),
            new((nint)202, "game client - Beta"),
            new((nint)303, "Notepad"),
            new((nint)101, "Game Client - Alpha")
        ]);
        var scope = new WindowScopeService(windows);

        var handles = scope.ResolveWindowHandles("^game client - (alpha|beta)$");

        Assert.Equal(new nint[] { 101, 202 }, handles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[")]
    public void ResolveWindowHandlesRejectsInvalidPattern(string pattern)
    {
        var scope = new WindowScopeService(new FakeWindowAutomationService([]));

        Assert.Throws<ArgumentException>(() => scope.ResolveWindowHandles(pattern));
    }

    [Fact]
    public async Task WindowScopeNodeStoresHandlesInExecutionContextAndVariable()
    {
        var executor = new WindowScopeNodeExecutor(new FakeWindowScopeService([(nint)101, (nint)202]));
        var node = WorkflowEngineTests.Node("scope", Contracts.Nodes.NodeTypes.WindowScope,
            ("titlePattern", "^Game"), ("outputVariable", "targets"));
        var context = new Contracts.Results.WorkflowExecutionContext
        {
            Workflow = new AutomationProject(),
            WaitIfPausedAsync = _ => Task.CompletedTask,
            Log = _ => { }
        };

        var result = await executor.ExecuteAsync(node, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new nint[] { 101, 202 }, context.WindowHandles);
        Assert.Equal(new long[] { 101, 202 }, Assert.IsType<long[]>(context.Variables["targets"]));
    }

    private sealed class FakeWindowAutomationService(IReadOnlyList<WindowInfo> windows) : IWindowAutomationService
    {
        public IReadOnlyList<WindowInfo> GetOpenWindows() => windows;
        public bool ActivateWindow(nint handle) => true;
        public bool CloseWindow(nint handle) => true;
        public bool MinimizeWindow(nint handle) => true;
        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int width, int height, int columns, int gapX, int gapY) => true;
        public bool SetWindowResolution(nint handle, int width, int height) => true;
        public WindowBounds GetWindowBounds(nint handle) => throw new NotSupportedException();
    }

    private sealed class FakeWindowScopeService(IReadOnlyList<nint> handles) : IWindowScopeService
    {
        public IReadOnlyList<nint> ResolveWindowHandles(string titlePattern) => handles;
    }
}
