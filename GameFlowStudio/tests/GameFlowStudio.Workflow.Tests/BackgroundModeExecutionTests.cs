using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Workflow.Tests;

public sealed class BackgroundModeExecutionTests
{
    private static readonly nint TargetWindow = (nint)0x1234;

    [Theory]
    [InlineData("mouseMove")]
    [InlineData("mouseDrag")]
    [InlineData("mouseScroll")]
    [InlineData("keyPress")]
    [InlineData("textInput")]
    public async Task BackgroundInputUsesMessageServiceWithoutForegroundInput(string nodeType)
    {
        var foreground = new RecordingForegroundInputService();
        var background = new RecordingBackgroundInputService();
        var executor = CreateInputExecutor(nodeType, foreground, background);
        var context = Context([TargetWindow]);

        var result = await executor.ExecuteAsync(
            InputNode(nodeType),
            context,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, foreground.TotalCalls);
        Assert.Equal(TargetWindow, background.WindowHandle);
        Assert.Equal(nodeType, background.Operation);

        switch (nodeType)
        {
            case NodeTypes.MouseMove:
                Assert.Equal((11, 12, 25), (background.X, background.Y, background.DurationMs));
                break;
            case NodeTypes.MouseDrag:
                Assert.Equal((1, 2, 31, 42), (background.X, background.Y, background.EndX, background.EndY));
                Assert.Equal(MouseButtonType.Right, background.Button);
                Assert.Equal(75, background.DurationMs);
                break;
            case NodeTypes.MouseScroll:
                Assert.Equal((13, 14, -240), (background.X, background.Y, background.Delta));
                break;
            case NodeTypes.KeyPress:
                Assert.Equal("F5", background.Value);
                Assert.Equal((50, 2, 10), (background.DurationMs, background.RepeatCount, background.IntervalMs));
                break;
            case NodeTypes.TextInput:
                Assert.Equal("测试 ABC", background.Value);
                Assert.Equal(7, background.IntervalMs);
                break;
        }
    }

    [Theory]
    [InlineData("mouseMove", 0)]
    [InlineData("mouseMove", 2)]
    [InlineData("mouseDrag", 0)]
    [InlineData("mouseDrag", 2)]
    [InlineData("mouseScroll", 0)]
    [InlineData("mouseScroll", 2)]
    [InlineData("keyPress", 0)]
    [InlineData("keyPress", 2)]
    [InlineData("textInput", 0)]
    [InlineData("textInput", 2)]
    public async Task BackgroundInputRequiresExactlyOneTargetWindow(string nodeType, int windowCount)
    {
        var foreground = new RecordingForegroundInputService();
        var background = new RecordingBackgroundInputService();
        var executor = CreateInputExecutor(nodeType, foreground, background);
        var handles = Enumerable.Range(1, windowCount).Select(value => (nint)value).ToArray();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(InputNode(nodeType), Context(handles), CancellationToken.None));

        Assert.Contains("唯一的目标窗口", error.Message);
        Assert.Equal(0, foreground.TotalCalls);
        Assert.Null(background.Operation);
    }

    [Fact]
    public async Task BackgroundCaptureUsesWindowCaptureAndCachesTheFrame()
    {
        var region = new CaptureRegion(10, 20, 30, 40);
        var desktop = new RecordingDesktopCaptureService();
        var window = new RecordingWindowCaptureService();
        var context = new DetectorEvaluationContext
        {
            Capture = desktop,
            WindowCapture = window,
            WindowHandles = [TargetWindow]
        };

        var first = await context.CaptureAsync(region, background: true, CancellationToken.None);
        var second = await context.CaptureAsync(region, background: true, CancellationToken.None);

        Assert.Same(window.Frame, first);
        Assert.Same(first, second);
        Assert.Equal(0, desktop.CaptureCalls);
        Assert.Equal(1, window.CaptureCalls);
        Assert.Equal(TargetWindow, window.WindowHandle);
        Assert.Equal(region, window.Region);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task BackgroundCaptureRequiresExactlyOneTargetWindow(int windowCount)
    {
        var desktop = new RecordingDesktopCaptureService();
        var window = new RecordingWindowCaptureService();
        var context = new DetectorEvaluationContext
        {
            Capture = desktop,
            WindowCapture = window,
            WindowHandles = Enumerable.Range(1, windowCount).Select(value => (nint)value).ToArray()
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.CaptureAsync(new CaptureRegion(1, 2, 3, 4), background: true, CancellationToken.None));

        Assert.Contains("唯一的目标窗口", error.Message);
        Assert.Equal(0, desktop.CaptureCalls);
        Assert.Equal(0, window.CaptureCalls);
    }

    [Fact]
    public async Task ForegroundCaptureStillUsesDesktopCapture()
    {
        var region = new CaptureRegion(10, 20, 30, 40);
        var desktop = new RecordingDesktopCaptureService();
        var window = new RecordingWindowCaptureService();
        var context = new DetectorEvaluationContext
        {
            Capture = desktop,
            WindowCapture = window,
            WindowHandles = [TargetWindow]
        };

        var frame = await context.CaptureAsync(region, background: false, CancellationToken.None);

        Assert.Same(desktop.Frame, frame);
        Assert.Equal(1, desktop.CaptureCalls);
        Assert.Equal(region, desktop.Region);
        Assert.Equal(0, window.CaptureCalls);
    }

    [Theory]
    [InlineData("closeProgram", true)]
    [InlineData("closeProgram", false)]
    [InlineData("minimizeWindow", true)]
    [InlineData("minimizeWindow", false)]
    [InlineData("setWindowResolution", true)]
    [InlineData("setWindowResolution", false)]
    public async Task WindowActionModeControlsActivation(string nodeType, bool backgroundMode)
    {
        nint[] handles = [(nint)101, (nint)202];
        var windows = new RecordingWindowAutomationService();
        var scopes = new FixedWindowScopeService(handles);
        var executor = CreateWindowActionExecutor(nodeType, windows, scopes);
        var node = Node(
            nodeType,
            ("windowActionMode", backgroundMode ? "background" : "foreground"),
            ("titlePattern", "^Game$"),
            ("width", 1280),
            ("height", 720));

        var result = await executor.ExecuteAsync(node, Context([]), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("^Game$", scopes.LastPattern);
        if (backgroundMode)
            Assert.Empty(windows.Activations);
        else
            Assert.Equal(handles, windows.Activations);

        switch (nodeType)
        {
            case NodeTypes.CloseProgram:
                Assert.Equal(handles, windows.Closed);
                Assert.Empty(windows.Minimized);
                Assert.Empty(windows.Resolutions);
                break;
            case NodeTypes.MinimizeWindow:
                Assert.Equal(handles, windows.Minimized);
                Assert.Empty(windows.Closed);
                Assert.Empty(windows.Resolutions);
                break;
            case NodeTypes.SetWindowResolution:
                Assert.Equal(
                    [(handles[0], 1280, 720), (handles[1], 1280, 720)],
                    windows.Resolutions);
                Assert.Empty(windows.Closed);
                Assert.Empty(windows.Minimized);
                break;
        }
    }

    private static INodeExecutor CreateInputExecutor(
        string nodeType,
        IInputAutomationService foreground,
        IBackgroundInputAutomationService background)
    {
        var coordinates = new PassthroughCoordinateResolver();
        return nodeType switch
        {
            NodeTypes.MouseMove => new MouseMoveNodeExecutor(foreground, coordinates, background),
            NodeTypes.MouseDrag => new MouseDragNodeExecutor(foreground, coordinates, background),
            NodeTypes.MouseScroll => new MouseScrollNodeExecutor(foreground, coordinates, background),
            NodeTypes.KeyPress => new KeyPressNodeExecutor(foreground, background),
            NodeTypes.TextInput => new TextInputNodeExecutor(foreground, background),
            _ => throw new ArgumentOutOfRangeException(nameof(nodeType), nodeType, null)
        };
    }

    private static INodeExecutor CreateWindowActionExecutor(
        string nodeType,
        IWindowAutomationService windows,
        IWindowScopeService scopes) =>
        nodeType switch
        {
            NodeTypes.CloseProgram => new CloseProgramNodeExecutor(windows, scopes),
            NodeTypes.MinimizeWindow => new MinimizeWindowNodeExecutor(windows, scopes),
            NodeTypes.SetWindowResolution => new SetWindowResolutionNodeExecutor(windows, scopes),
            _ => throw new ArgumentOutOfRangeException(nameof(nodeType), nodeType, null)
        };

    private static WorkflowNode InputNode(string nodeType) =>
        Node(
            nodeType,
            ("inputMode", "background"),
            ("coordinateMode", "absolute"),
            ("x", nodeType == NodeTypes.MouseScroll ? 13 : 11),
            ("y", nodeType == NodeTypes.MouseScroll ? 14 : 12),
            ("durationMs", nodeType == NodeTypes.MouseDrag ? 75 : nodeType == NodeTypes.KeyPress ? 50 : 25),
            ("startX", 1),
            ("startY", 2),
            ("endX", 31),
            ("endY", 42),
            ("button", "right"),
            ("delta", -240),
            ("key", "F5"),
            ("repeatCount", 2),
            ("intervalMs", nodeType == NodeTypes.TextInput ? 7 : 10),
            ("text", "测试 ABC"));

    private static WorkflowNode Node(string type, params (string Name, object Value)[] parameters) => new()
    {
        Id = $"{type}-test",
        Type = type,
        Data = new()
        {
            DisplayName = type,
            Parameters = parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => ProjectJson.Element(parameter.Value))
        }
    };

    private static WorkflowExecutionContext Context(IReadOnlyList<nint> handles) => new()
    {
        Workflow = new AutomationProject(),
        WindowHandles = handles,
        WaitIfPausedAsync = _ => Task.CompletedTask,
        Log = _ => { }
    };

    private sealed class PassthroughCoordinateResolver : IMouseCoordinateResolver
    {
        public (int X, int Y) Resolve(
            int x,
            int y,
            CoordinateMode mode,
            WorkflowExecutionContext context) => (x, y);
    }

    private sealed class RecordingForegroundInputService : IInputAutomationService
    {
        public int TotalCalls { get; private set; }

        public Task MoveMouseAsync(int x, int y, int durationMs, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task ClickAsync(
            int x,
            int y,
            MouseButtonType button,
            int clickCount,
            int intervalMs,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task DragMouseAsync(
            int startX,
            int startY,
            int endX,
            int endY,
            MouseButtonType button,
            int durationMs,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task ScrollMouseAsync(int x, int y, int delta, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task PressKeyAsync(
            string key,
            int durationMs,
            int repeatCount,
            int intervalMs,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task TypeTextAsync(string text, int intervalMs, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBackgroundInputService : IBackgroundInputAutomationService
    {
        public string? Operation { get; private set; }
        public nint WindowHandle { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public int EndX { get; private set; }
        public int EndY { get; private set; }
        public MouseButtonType Button { get; private set; }
        public int Delta { get; private set; }
        public int DurationMs { get; private set; }
        public int RepeatCount { get; private set; }
        public int IntervalMs { get; private set; }
        public string? Value { get; private set; }

        public Task MoveMouseAsync(
            nint windowHandle,
            int screenX,
            int screenY,
            int durationMs,
            CancellationToken cancellationToken)
        {
            Record(NodeTypes.MouseMove, windowHandle);
            X = screenX;
            Y = screenY;
            DurationMs = durationMs;
            return Task.CompletedTask;
        }

        public Task ClickAsync(
            nint windowHandle,
            int screenX,
            int screenY,
            MouseButtonType button,
            int clickCount,
            int intervalMs,
            CancellationToken cancellationToken)
        {
            Record(NodeTypes.MouseClick, windowHandle);
            X = screenX;
            Y = screenY;
            Button = button;
            RepeatCount = clickCount;
            IntervalMs = intervalMs;
            return Task.CompletedTask;
        }

        public Task DragMouseAsync(
            nint windowHandle,
            int startScreenX,
            int startScreenY,
            int endScreenX,
            int endScreenY,
            MouseButtonType button,
            int durationMs,
            CancellationToken cancellationToken)
        {
            Record(NodeTypes.MouseDrag, windowHandle);
            X = startScreenX;
            Y = startScreenY;
            EndX = endScreenX;
            EndY = endScreenY;
            Button = button;
            DurationMs = durationMs;
            return Task.CompletedTask;
        }

        public Task ScrollMouseAsync(
            nint windowHandle,
            int screenX,
            int screenY,
            int delta,
            CancellationToken cancellationToken)
        {
            Record(NodeTypes.MouseScroll, windowHandle);
            X = screenX;
            Y = screenY;
            Delta = delta;
            return Task.CompletedTask;
        }

        public Task PressKeyAsync(
            nint windowHandle,
            string key,
            int durationMs,
            int repeatCount,
            int intervalMs,
            CancellationToken cancellationToken)
        {
            Record(NodeTypes.KeyPress, windowHandle);
            Value = key;
            DurationMs = durationMs;
            RepeatCount = repeatCount;
            IntervalMs = intervalMs;
            return Task.CompletedTask;
        }

        public Task TypeTextAsync(
            nint windowHandle,
            string text,
            int intervalMs,
            CancellationToken cancellationToken)
        {
            Record(NodeTypes.TextInput, windowHandle);
            Value = text;
            IntervalMs = intervalMs;
            return Task.CompletedTask;
        }

        private void Record(string operation, nint windowHandle)
        {
            Operation = operation;
            WindowHandle = windowHandle;
        }
    }

    private sealed class RecordingDesktopCaptureService : IDesktopCaptureService
    {
        public CapturedFrame Frame { get; } = new(1, 2, 1, 1, [1, 2, 3, 4]);
        public int CaptureCalls { get; private set; }
        public CaptureRegion? Region { get; private set; }
        public CaptureRegion VirtualScreenBounds => new(0, 0, 1920, 1080);

        public Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken)
        {
            CaptureCalls++;
            Region = region;
            return Task.FromResult(Frame);
        }
    }

    private sealed class RecordingWindowCaptureService : IWindowCaptureService
    {
        public CapturedFrame Frame { get; } = new(10, 20, 1, 1, [4, 3, 2, 1]);
        public int CaptureCalls { get; private set; }
        public nint WindowHandle { get; private set; }
        public CaptureRegion? Region { get; private set; }

        public Task<CapturedFrame> CaptureAsync(
            nint windowHandle,
            CaptureRegion region,
            CancellationToken cancellationToken)
        {
            CaptureCalls++;
            WindowHandle = windowHandle;
            Region = region;
            return Task.FromResult(Frame);
        }
    }

    private sealed class FixedWindowScopeService(IReadOnlyList<nint> handles) : IWindowScopeService
    {
        public string? LastPattern { get; private set; }

        public IReadOnlyList<nint> ResolveWindowHandles(string titlePattern)
        {
            LastPattern = titlePattern;
            return handles;
        }
    }

    private sealed class RecordingWindowAutomationService : IWindowAutomationService
    {
        public List<nint> Activations { get; } = [];
        public List<nint> Closed { get; } = [];
        public List<nint> Minimized { get; } = [];
        public List<(nint Handle, int Width, int Height)> Resolutions { get; } = [];

        public IReadOnlyList<WindowInfo> GetOpenWindows() => [];

        public bool ActivateWindow(nint handle)
        {
            Activations.Add(handle);
            return true;
        }

        public bool CloseWindow(nint handle)
        {
            Closed.Add(handle);
            return true;
        }

        public bool MinimizeWindow(nint handle)
        {
            Minimized.Add(handle);
            return true;
        }

        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int offsetY) => true;

        public bool SetWindowResolution(nint handle, int width, int height)
        {
            Resolutions.Add((handle, width, height));
            return true;
        }

        public WindowBounds GetWindowBounds(nint handle) => throw new NotSupportedException();
    }
}
