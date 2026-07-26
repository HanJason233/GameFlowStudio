using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Workflow.Tests;

public sealed class MouseClickExecutionTests
{
    [Fact]
    public async Task RelativeClickAddsWindowOriginToOffset()
    {
        var input = new RecordingInputAutomationService();
        var windows = new FakeWindowAutomationService(new(101, 100, 200, 900, 800));
        var executor = new MouseClickNodeExecutor(input, new MouseClickCoordinateResolver(windows), windows);
        var node = WorkflowEngineTests.Node("click", Contracts.Nodes.NodeTypes.MouseClick,
            ("coordinateMode", "windowRelative"), ("x", 25), ("y", 35), ("button", "left"),
            ("clickCount", 1), ("intervalMs", 0), ("windowHandleVariable", "targets"), ("windowHandleIndex", 0));
        var context = Context();
        context.Variables["targets"] = new long[] { 101 };

        await executor.ExecuteAsync(node, context, CancellationToken.None);

        Assert.Equal((125, 235), (input.X, input.Y));
        Assert.Equal(MouseButtonType.Left, input.Button);
        Assert.Equal((nint)101, windows.ActivatedHandle);
    }

    [Fact]
    public async Task ScreenClickDoesNotUseWindowBounds()
    {
        var input = new RecordingInputAutomationService();
        var executor = new MouseClickNodeExecutor(input, new MouseClickCoordinateResolver(new FakeWindowAutomationService(new(101, 100, 200, 900, 800))));
        var node = WorkflowEngineTests.Node("click", Contracts.Nodes.NodeTypes.MouseClick,
            ("x", 25), ("y", 35), ("button", "right"), ("clickCount", 1), ("intervalMs", 0));

        await executor.ExecuteAsync(node, Context(), CancellationToken.None);

        Assert.Equal((25, 35), (input.X, input.Y));
        Assert.Equal(MouseButtonType.Right, input.Button);
    }

    private static WorkflowExecutionContext Context() => new()
    {
        Workflow = new AutomationProject(),
        WaitIfPausedAsync = _ => Task.CompletedTask,
        Log = _ => { }
    };

    private sealed class FakeWindowAutomationService(WindowBounds bounds) : IWindowAutomationService
    {
        public nint? ActivatedHandle { get; private set; }
        public IReadOnlyList<WindowInfo> GetOpenWindows() => [];
        public bool ActivateWindow(nint handle) { ActivatedHandle = handle; return true; }
        public bool CloseWindow(nint handle) => true;
        public bool ResizeWindow(nint handle, int width, int height) => true;
        public bool MinimizeWindow(nint handle) => true;
        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int width, int height, int columns, int gapX, int gapY) => true;
        public bool SetWindowResolution(nint handle, int width, int height) => true;
        public WindowBounds GetWindowBounds(nint handle) => bounds with { Handle = handle };
    }

    private sealed class RecordingInputAutomationService : IInputAutomationService
    {
        public int X { get; private set; }
        public int Y { get; private set; }
        public MouseButtonType Button { get; private set; }

        public Task MoveMouseAsync(int x, int y, int durationMs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DragMouseAsync(int startX, int startY, int endX, int endY, MouseButtonType button, int durationMs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ScrollMouseAsync(int x, int y, int delta, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClickAsync(int x, int y, MouseButtonType button, int clickCount, int intervalMs, CancellationToken cancellationToken)
        {
            X = x; Y = y; Button = button;
            return Task.CompletedTask;
        }

        public Task PressKeyAsync(string key, int durationMs, int repeatCount, int intervalMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TypeTextAsync(string text, int intervalMs, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
