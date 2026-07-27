using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Detectors;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Workflow.Tests;

public sealed class StateConditionTests
{
    [Fact]
    public async Task ColorDetectorReadsBgraPixels()
    {
        var capture = new FakeCapture(new CapturedFrame(0, 0, 2, 1, [0, 0, 255, 255, 0, 0, 255, 255]));
        var detector = new DetectorDefinition { Id = "red", Type = DetectorType.Color };
        detector.Parameters["width"] = ProjectJson.Element(2);
        detector.Parameters["height"] = ProjectJson.Element(1);
        detector.Parameters["targetColor"] = ProjectJson.Element("#FF0000");
        detector.Parameters["minMatchRatio"] = ProjectJson.Element(1d);

        var service = new StateDetectorService([new ColorStateDetector()]);
        var result = await service.EvaluateAsync(detector, Context(capture), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(1, result.State);
    }

    [Fact]
    public async Task MultipleRulesUseAndSemantics()
    {
        var capture = new FakeCapture(new CapturedFrame(0, 0, 2, 1, [0, 0, 255, 255, 0, 255, 0, 255]));
        var red = Color("red", "#FF0000");
        var green = Color("green", "#00FF00");
        var evaluator = new StateConditionEvaluator(new StateDetectorService([new ColorStateDetector()]), capture);
        var matched = await evaluator.EvaluateAsync([red, green], [], CancellationToken.None);
        Assert.True(matched.Matched);

        var notMatched = await evaluator.EvaluateAsync([red, Color("green", "#0000FF")], [], CancellationToken.None);
        Assert.False(notMatched.Matched);
    }

    [Fact]
    public async Task RelativeColorDetectorOffsetsCaptureRegionFromScriptWindow()
    {
        var capture = new FakeCapture(new CapturedFrame(105, 206, 1, 1, [0, 0, 255, 255]));
        var detector = new DetectorDefinition { Id = "red", Type = DetectorType.Color };
        detector.Parameters["coordinateMode"] = ProjectJson.Element("relative");
        detector.Parameters["x"] = ProjectJson.Element(5);
        detector.Parameters["y"] = ProjectJson.Element(6);
        detector.Parameters["width"] = ProjectJson.Element(1);
        detector.Parameters["height"] = ProjectJson.Element(1);
        detector.Parameters["targetColor"] = ProjectJson.Element("#FF0000");
        var context = new DetectorEvaluationContext
        {
            Capture = capture,
            Windows = new FakeWindows(new(101, 100, 200, 900, 800)),
            WindowHandles = [(nint)101]
        };

        var result = await new StateDetectorService([new ColorStateDetector()])
            .EvaluateAsync(detector, context, CancellationToken.None);

        Assert.Equal(1, result.State);
        Assert.Equal(new CaptureRegion(105, 206, 1, 1), capture.LastRegion);
    }

    [Fact]
    public async Task SingleColorConditionUsesOneDetector()
    {
        var capture = new FakeCapture(new CapturedFrame(0, 0, 1, 1, [0, 0, 255, 255]));
        var evaluator = new StateConditionEvaluator(new StateDetectorService([new ColorStateDetector()]), capture);
        var logs = new List<WorkflowLogEntry>();
        var node = new WorkflowNode
        {
            Id = "color-condition",
            Type = NodeTypes.ColorCondition,
            Data = new()
            {
                DisplayName = "色判断",
                Parameters = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["x"] = ProjectJson.Element(0),
                    ["y"] = ProjectJson.Element(0),
                    ["width"] = ProjectJson.Element(1),
                    ["height"] = ProjectJson.Element(1),
                    ["targetColor"] = ProjectJson.Element("#FF0000"),
                    ["tolerance"] = ProjectJson.Element(10),
                    ["minMatchRatio"] = ProjectJson.Element(1d),
                }
            }
        };
        var context = new WorkflowExecutionContext
        {
            Workflow = new AutomationProject(),
            WaitIfPausedAsync = _ => Task.CompletedTask,
            Log = logs.Add
        };

        var result = await new SingleStateConditionNodeExecutor(NodeTypes.ColorCondition, DetectorType.Color, evaluator)
            .ExecuteAsync(node, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("true", result.NextHandle);
        Assert.Contains(logs, entry => entry.Message.Contains("实际色值") && entry.Message.Contains("匹配比例"));
    }

    [Fact]
    public async Task JointConditionReadsDetectorsFromItsOwnParameters()
    {
        var capture = new FakeCapture(new CapturedFrame(0, 0, 1, 1, [0, 0, 255, 255]));
        var evaluator = new StateConditionEvaluator(new StateDetectorService([new ColorStateDetector()]), capture);
        var detector = Color("red", "#FF0000");
        var node = new WorkflowNode
        {
            Id = "state-condition",
            Type = NodeTypes.StateCondition,
            Data = new()
            {
                DisplayName = "联合判断",
                Parameters = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["detectors"] = ProjectJson.Element(new[] { detector })
                }
            }
        };
        var context = new WorkflowExecutionContext
        {
            Workflow = new AutomationProject(),
            WaitIfPausedAsync = _ => Task.CompletedTask,
            Log = _ => { }
        };

        var result = await new StateConditionNodeExecutor(evaluator).ExecuteAsync(node, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("true", result.NextHandle);
    }

    [Fact]
    public async Task ImageDetectorFindsTemplate()
    {
        var capture = new FakeCapture(new CapturedFrame(0, 0, 2, 1, [0, 0, 255, 255, 0, 255, 0, 255]));
        var images = new FakeImages(new DetectorImage(1, 1, [0, 255, 0, 255]));
        var detector = new DetectorDefinition { Id = "image", Type = DetectorType.Image };
        detector.Parameters["templatePath"] = ProjectJson.Element("template.png");
        var service = new StateDetectorService([new ImageStateDetector()]);
        var result = await service.EvaluateAsync(detector, new DetectorEvaluationContext { Capture = capture, Images = images }, CancellationToken.None);

        Assert.Equal(1, result.State);
        Assert.Contains("最佳匹配置信度", result.Message);
        Assert.Contains("结果 匹配", result.Message);
    }

    [Fact]
    public async Task OcrDetectorMatchesTextAtConfiguredThreshold()
    {
        var capture = new FakeCapture(new CapturedFrame(10, 20, 4, 3, new byte[4 * 3 * 4]));
        var detector = new DetectorDefinition { Id = "ocr", Type = DetectorType.Ocr };
        detector.Parameters["coordinateMode"] = ProjectJson.Element("absolute");
        detector.Parameters["x"] = ProjectJson.Element(12);
        detector.Parameters["y"] = ProjectJson.Element(23);
        detector.Parameters["width"] = ProjectJson.Element(2);
        detector.Parameters["height"] = ProjectJson.Element(1);
        detector.Parameters["targetText"] = ProjectJson.Element("Game Start");
        detector.Parameters["matchThreshold"] = ProjectJson.Element(.9d);

        var result = await new StateDetectorService([new OcrStateDetector(new FakeOcr("GameStart"))])
            .EvaluateAsync(detector, Context(capture), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(1, result.State);
        Assert.Contains("识别文本", result.Message);
        Assert.Contains("目标文本", result.Message);
        Assert.Contains("结果 匹配", result.Message);
        Assert.Equal(new CaptureRegion(12, 23, 2, 1), capture.LastRegion);
    }

    [Theory]
    [InlineData(DetectorType.Ocr)]
    [InlineData(DetectorType.AiRecognition)]
    public async Task UnsupportedDetectorsCannotMatch(DetectorType type)
    {
        var detector = new DetectorDefinition { Id = "todo", Type = type };
        var service = new StateDetectorService([new TodoStateDetector(type)]);
        var result = await service.EvaluateAsync(detector, Context(new FakeCapture(new CapturedFrame(0, 0, 1, 1, [0, 0, 0, 255]))), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal(0, result.State);
    }

    private static DetectorDefinition Color(string id, string value)
    {
        var detector = new DetectorDefinition { Id = id, Type = DetectorType.Color };
        detector.Parameters["width"] = ProjectJson.Element(2);
        detector.Parameters["height"] = ProjectJson.Element(1);
        detector.Parameters["targetColor"] = ProjectJson.Element(value);
        detector.Parameters["minMatchRatio"] = ProjectJson.Element(.5d);
        return detector;
    }

    private static DetectorEvaluationContext Context(FakeCapture capture) => new() { Capture = capture };

    private sealed class FakeCapture(CapturedFrame frame) : IDesktopCaptureService
    {
        public CaptureRegion? LastRegion { get; private set; }
        public CaptureRegion VirtualScreenBounds => new(frame.X, frame.Y, frame.Width, frame.Height);
        public Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken)
        {
            LastRegion = region;
            return Task.FromResult(frame);
        }
    }

    private sealed class FakeWindows(WindowBounds bounds) : IWindowAutomationService
    {
        public IReadOnlyList<WindowInfo> GetOpenWindows() => [];
        public bool ActivateWindow(nint handle) => true;
        public bool CloseWindow(nint handle) => true;
        public bool ResizeWindow(nint handle, int width, int height) => true;
        public bool MinimizeWindow(nint handle) => true;
        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int width, int height, int columns, int gapX, int gapY) => true;
        public bool SetWindowResolution(nint handle, int width, int height) => true;
        public WindowBounds GetWindowBounds(nint handle) => bounds with { Handle = handle };
    }

    private sealed class FakeImages(DetectorImage image) : IImageTemplateProvider
    {
        public Task<DetectorImage> LoadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(image);
    }

    private sealed class FakeOcr(string text) : IOcrService
    {
        public Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrRecognitionResult(text));
    }
}
