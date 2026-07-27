using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Contracts.States;

namespace GameFlowStudio.Contracts.Services;

/// <summary>验证并异步执行脚本工作流。</summary>
public interface IWorkflowRunner
{
    WorkflowExecutionState State { get; }
    int DefaultNodeDelayMs { get; set; }
    event EventHandler<NodeExecutionEventArgs>? NodeStarted;
    event EventHandler<NodeExecutionEventArgs>? NodeCompleted;
    event EventHandler<NodeExecutionEventArgs>? NodeFailed;
    event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;
    Task RunAsync(AutomationProject workflow, CancellationToken cancellationToken);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}

/// <summary>捕获桌面像素。实现层负责处理 DPI、多显示器和屏幕坐标。</summary>
public interface IDesktopCaptureService
{
    CaptureRegion VirtualScreenBounds { get; }
    Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken);
}

/// <summary>读取图片检测器使用的模板图片。</summary>
public interface IImageTemplateProvider
{
    Task<DetectorImage> LoadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>在指定屏幕区域执行 OCR。具体引擎由宿主注入，可接入云端或其他 OCR 服务。</summary>
public interface IOcrService
{
    Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken);
}

/// <summary>可根据目标文本在多个 OCR 预处理结果中选择更合适结果的 OCR 服务。</summary>
public interface ITargetAwareOcrService
{
    Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, string targetText, CancellationToken cancellationToken);
}

public interface IStateDetector
{
    DetectorType Type { get; }
    Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken);
}

public interface IStateDetectorService
{
    Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken);
}

public interface IStateConditionEvaluator
{
    Task<StateConditionEvaluation> EvaluateAsync(
        IReadOnlyList<DetectorDefinition> detectors,
        IReadOnlyList<nint> windowHandles,
        CancellationToken cancellationToken);
}

/// <summary>执行一种节点类型。</summary>
public interface INodeExecutor
{
    string NodeType { get; }
    Task<NodeExecutionResult> ExecuteAsync(WorkflowNode node, WorkflowExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>执行前验证工作流结构和参数。</summary>
public interface IWorkflowValidator
{
    WorkflowValidationResult Validate(AutomationProject workflow);
}

/// <summary>隔离 Windows 输入自动化。</summary>
public interface IInputAutomationService
{
    Task MoveMouseAsync(int x, int y, int durationMs, CancellationToken cancellationToken);
    Task ClickAsync(int x, int y, MouseButtonType button, int clickCount, int intervalMs, CancellationToken cancellationToken);
    Task DragMouseAsync(int startX, int startY, int endX, int endY, MouseButtonType button, int durationMs, CancellationToken cancellationToken);
    Task ScrollMouseAsync(int x, int y, int delta, CancellationToken cancellationToken);
    Task PressKeyAsync(string key, int durationMs, int repeatCount, int intervalMs, CancellationToken cancellationToken);
    Task TypeTextAsync(string text, int intervalMs, CancellationToken cancellationToken);
}

public interface IWindowAutomationService
{
    IReadOnlyList<WindowInfo> GetOpenWindows();
    bool ActivateWindow(nint handle);
    bool CloseWindow(nint handle);
    bool ResizeWindow(nint handle, int width, int height);
    bool MinimizeWindow(nint handle);
    bool ArrangeWindows(IReadOnlyList<nint> handles, int x, int y, int width, int height, int columns, int gapX, int gapY);
    bool SetWindowResolution(nint handle, int width, int height);
    WindowBounds GetWindowBounds(nint handle);
}

public interface IProgramAutomationService
{
    Task StartAsync(string filePath, string arguments, string workingDirectory, bool waitForExit, CancellationToken cancellationToken);
}

/// <summary>通过窗口标题规则解析自动化操作的目标窗口作用域。</summary>
public interface IWindowScopeService
{
    IReadOnlyList<nint> ResolveWindowHandles(string titlePattern);
}

public interface IProjectStorageService
{
    Task SaveAsync(AutomationProject project, string filePath, CancellationToken cancellationToken);
    Task<AutomationProject> LoadAsync(string filePath, CancellationToken cancellationToken);
}

public interface ISettingsStorageService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public double ToolboxPaneWidth { get; set; } = 220;
    public double PropertiesPaneWidth { get; set; } = 320;
    public double OutputPaneHeight { get; set; } = 220;
    public bool IsProjectFilesPaneVisible { get; set; } = false;
    public bool IsScriptListPaneVisible { get; set; } = true;
    public bool IsToolboxPaneVisible { get; set; } = true;
    public bool IsPropertiesPaneVisible { get; set; } = false;
    public bool IsOutputPaneVisible { get; set; } = true;
    public string StepCategoryLayout { get; set; } = "vertical";
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public string DefaultProjectDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string GeminiApiKey { get; set; } = string.Empty;
    public string RunHotkey { get; set; } = "F8";
    public string PauseHotkey { get; set; } = "F9";
    public string StopHotkey { get; set; } = "F10";
    public string Theme { get; set; } = "dark";
}
