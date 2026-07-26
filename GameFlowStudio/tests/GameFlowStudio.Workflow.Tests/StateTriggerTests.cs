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
        var rules = new List<StateDetectorRule> { new() { DetectorId = "red", State = 1 }, new() { DetectorId = "green", State = 1 } };

        var matched = await evaluator.EvaluateAsync([red, green], rules, CancellationToken.None);
        Assert.True(matched.Matched);

        rules[1].State = 0;
        var notMatched = await evaluator.EvaluateAsync([red, green], rules, CancellationToken.None);
        Assert.False(notMatched.Matched);
    }

    [Fact]
    public async Task SingleColorConditionUsesOneDetector()
    {
        var capture = new FakeCapture(new CapturedFrame(0, 0, 1, 1, [0, 0, 255, 255]));
        var evaluator = new StateConditionEvaluator(new StateDetectorService([new ColorStateDetector()]), capture);
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
                    ["state"] = ProjectJson.Element(1)
                }
            }
        };
        var context = new WorkflowExecutionContext
        {
            Workflow = new AutomationProject(),
            WaitIfPausedAsync = _ => Task.CompletedTask,
            Log = _ => { }
        };

        var result = await new SingleStateConditionNodeExecutor(NodeTypes.ColorCondition, DetectorType.Color, evaluator)
            .ExecuteAsync(node, context, CancellationToken.None);

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
        public CaptureRegion VirtualScreenBounds => new(frame.X, frame.Y, frame.Width, frame.Height);
        public Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken) => Task.FromResult(frame);
    }

    private sealed class FakeImages(DetectorImage image) : IImageTemplateProvider
    {
        public Task<DetectorImage> LoadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(image);
    }
}
