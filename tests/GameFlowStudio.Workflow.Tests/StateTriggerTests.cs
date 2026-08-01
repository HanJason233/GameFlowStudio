using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Workflow.Detectors;
using GameFlowStudio.Workflow.Executors;

namespace GameFlowStudio.Workflow.Tests;

public sealed class DetectorConditionTests
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
        var evaluator = new DetectorEvaluator(new StateDetectorService([new ColorStateDetector()]), capture);
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

        var result = await new SingleConditionNodeExecutor(NodeTypes.ColorCondition, DetectorType.Color, evaluator)
            .ExecuteAsync(node, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("true", result.NextHandle);
        Assert.Contains(logs, entry => entry.Message.Contains("实际色值") && entry.Message.Contains("匹配比例"));
    }

    [Fact]
    public async Task UnsupportedConditionFailsInsteadOfTakingFalseBranch()
    {
        var node = new WorkflowNode
        {
            Id = "unsupported-condition",
            Type = NodeTypes.OcrCondition,
            Data = new() { DisplayName = "OCR 判断" }
        };
        var context = new WorkflowExecutionContext
        {
            Workflow = new AutomationProject(),
            WaitIfPausedAsync = _ => Task.CompletedTask,
            Log = _ => { }
        };
        var evaluator = new StubEvaluator(
            new(node.Id, DetectorType.Ocr, 0, Supported: false, Message: "OCR 服务不可用"));

        var result = await new SingleConditionNodeExecutor(NodeTypes.OcrCondition, DetectorType.Ocr, evaluator)
            .ExecuteAsync(node, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("OCR 服务不可用", result.ErrorMessage);
    }

    [Fact]
    public void CapturedFrameReturnsEmptyPixelForIncompleteBuffer()
    {
        var frame = new CapturedFrame(0, 0, 2, 1, [0, 0, 0, 255]);

        Assert.Equal(4, frame.Pixel(0, 0).Length);
        Assert.Equal(0, frame.Pixel(1, 0).Length);
        Assert.Equal(0, new CapturedFrame(0, 0, -1, 1, [0, 0, 0, 255]).Pixel(0, 0).Length);
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
    public async Task ImageDetectorCanUseOpenCvTemplateMatching()
    {
        var black = new byte[] { 0, 0, 0, 255 };
        var framePixels = black.Concat(black).Concat(black).Concat(black)
            .Concat(black).Concat(new byte[] { 10, 20, 30, 255 }).Concat(new byte[] { 40, 50, 60, 255 }).Concat(black)
            .Concat(black).Concat(new byte[] { 70, 80, 90, 255 }).Concat(new byte[] { 100, 110, 120, 255 }).Concat(black)
            .ToArray();
        var templatePixels = new byte[]
        {
            10, 20, 30, 255, 40, 50, 60, 255,
            70, 80, 90, 255, 100, 110, 120, 255,
        };
        var capture = new FakeCapture(new CapturedFrame(0, 0, 4, 3, framePixels));
        var images = new FakeImages(new DetectorImage(2, 2, templatePixels));
        var detector = new DetectorDefinition { Id = "opencv-image", Type = DetectorType.Image };
        detector.Parameters["templatePath"] = ProjectJson.Element("template.png");
        detector.Parameters["imageMatchEngine"] = ProjectJson.Element("opencv");
        detector.Parameters["matchThreshold"] = ProjectJson.Element(.99d);

        var result = await new StateDetectorService([new ImageStateDetector()])
            .EvaluateAsync(detector, new DetectorEvaluationContext { Capture = capture, Images = images }, CancellationToken.None);

        Assert.Equal(1, result.State);
        Assert.Contains("结果 匹配", result.Message);
    }

    [Fact]
    public async Task ImageDetectorCanSearchCurrentWindowWithoutCoordinates()
    {
        var black = new byte[] { 0, 0, 0, 255 };
        var framePixels = black.Concat(black).Concat(black).Concat(black)
            .Concat(black).Concat(new byte[] { 10, 20, 30, 255 }).Concat(new byte[] { 40, 50, 60, 255 }).Concat(black)
            .Concat(black).Concat(new byte[] { 70, 80, 90, 255 }).Concat(new byte[] { 100, 110, 120, 255 }).Concat(black)
            .ToArray();
        var templatePixels = new byte[]
        {
            10, 20, 30, 255, 40, 50, 60, 255,
            70, 80, 90, 255, 100, 110, 120, 255,
        };
        var capture = new FakeCapture(new CapturedFrame(105, 206, 4, 3, framePixels));
        var images = new FakeImages(new DetectorImage(2, 2, templatePixels));
        var detector = new DetectorDefinition { Id = "current-window-image", Type = DetectorType.Image };
        detector.Parameters["imageSearchMode"] = ProjectJson.Element("current");
        detector.Parameters["templatePath"] = ProjectJson.Element("template.png");
        detector.Parameters["matchThreshold"] = ProjectJson.Element(.99d);
        var windows = new FakeWindows(new WindowBounds((nint)101, 100, 200, 900, 800));

        var result = await new StateDetectorService([new ImageStateDetector()])
            .EvaluateAsync(detector, new DetectorEvaluationContext
            {
                Capture = capture,
                Images = images,
                Windows = windows,
                WindowHandles = [(nint)101]
            }, CancellationToken.None);

        Assert.Equal(1, result.State);
        Assert.Equal(new CaptureRegion(100, 200, 800, 600), capture.LastRegion);
        Assert.Contains("当前操作范围", result.Message);
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

    [Fact]
    public async Task OcrDetectorCanSearchCurrentWindowWithoutCoordinates()
    {
        var capture = new FakeCapture(new CapturedFrame(105, 206, 4, 3, new byte[4 * 3 * 4]));
        var detector = new DetectorDefinition { Id = "current-window-ocr", Type = DetectorType.Ocr };
        detector.Parameters["textSearchMode"] = ProjectJson.Element("current");
        detector.Parameters["ocrEngine"] = ProjectJson.Element("gemini");
        detector.Parameters["targetText"] = ProjectJson.Element("Game Start");
        detector.Parameters["matchThreshold"] = ProjectJson.Element(.9d);
        var windows = new FakeWindows(new WindowBounds((nint)101, 100, 200, 900, 800));

        var result = await new StateDetectorService([new OcrStateDetector(new FakeOcr("GameStart"))])
            .EvaluateAsync(detector, new DetectorEvaluationContext
            {
                Capture = capture,
                Windows = windows,
                WindowHandles = [(nint)101]
            }, CancellationToken.None);

        Assert.Equal(1, result.State);
        Assert.Equal(new CaptureRegion(100, 200, 800, 600), capture.LastRegion);
        Assert.Contains("当前操作范围", result.Message);
    }

    [Fact]
    public async Task LocalOcrEngineReportsPlaceholderStatus()
    {
        var detector = new DetectorDefinition { Id = "local-ocr", Type = DetectorType.Ocr };
        detector.Parameters["ocrEngine"] = ProjectJson.Element("local");
        detector.Parameters["targetText"] = ProjectJson.Element("Game Start");
        var service = new StateDetectorService([new OcrStateDetector(new FakeOcr("unused"), new LocalOcrPlaceholderService())]);

        var result = await service.EvaluateAsync(
            detector,
            Context(new FakeCapture(new CapturedFrame(0, 0, 1, 1, [0, 0, 0, 255]))),
            CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Contains("本地 OCR 尚未实现", result.Message);
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
        public bool MinimizeWindow(nint handle) => true;
        public bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int offsetY) => true;
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

    private sealed class StubEvaluator(DetectorEvaluationResult result) : IDetectorEvaluator
    {
        public Task<DetectorEvaluationResult> EvaluateAsync(
            DetectorDefinition detector,
            IReadOnlyList<nint> windowHandles,
            ScriptInteractionMode scriptInteractionMode,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
