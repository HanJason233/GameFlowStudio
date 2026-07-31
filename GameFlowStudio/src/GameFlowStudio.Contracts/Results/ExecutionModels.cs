using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;

namespace GameFlowStudio.Contracts.Results;

/// <summary>跨脚本跳转目标。</summary>
public sealed record WorkflowJumpTarget(string ScriptId, string ActionId);

/// <summary>节点执行结果；NextHandle 决定采用哪个输出句柄，JumpTarget 用于跨脚本跳转。</summary>
public sealed record NodeExecutionResult(bool Success, string NextHandle = "output", string? ErrorMessage = null, WorkflowJumpTarget? JumpTarget = null)
{
    public static NodeExecutionResult Ok(string nextHandle = "output") => new(true, nextHandle);
    public static NodeExecutionResult Jump(string scriptId, string actionId) => new(true, JumpTarget: new(scriptId, actionId));
    public static NodeExecutionResult Fail(string error) => new(false, "output", error);
}

public sealed record WorkflowValidationError(string Code, string Message, string? NodeId = null, string? EdgeId = null);

public sealed class WorkflowValidationResult
{
    public List<WorkflowValidationError> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

public sealed class WorkflowExecutionContext
{
    public required AutomationProject Workflow { get; init; }
    public ScriptInteractionMode ScriptInteractionMode { get; set; } = ScriptInteractionMode.Foreground;
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<nint> WindowHandles { get; set; } = [];
    public required Func<CancellationToken, Task> WaitIfPausedAsync { get; init; }
    public required Action<WorkflowLogEntry> Log { get; init; }
}

public sealed record WindowInfo(nint Handle, string Title);

public sealed record WindowBounds(nint Handle, int Left, int Top, int Right, int Bottom);

public sealed record WorkflowLogEntry(
    DateTimeOffset Timestamp,
    WorkflowLogLevel Level,
    string WorkflowId,
    string? NodeId,
    string? NodeName,
    string Message,
    string? Exception = null,
    long? ElapsedMilliseconds = null);

public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>BGRA32、从左到右从上到下排列的屏幕帧。</summary>
public sealed record CapturedFrame(int X, int Y, int Width, int Height, byte[] Bgra32)
{
    public ReadOnlySpan<byte> Pixel(int x, int y)
    {
        if (Width <= 0 || Height <= 0 ||
            (uint)x >= (uint)Width || (uint)y >= (uint)Height ||
            Bgra32 is null)
            return ReadOnlySpan<byte>.Empty;

        var offset = ((long)y * Width + x) * 4;
        return offset >= 0 && offset <= Bgra32.Length - 4
            ? Bgra32.AsSpan((int)offset, 4)
            : ReadOnlySpan<byte>.Empty;
    }
}

/// <summary>BGRA32、从左到右从上到下排列的模板图片。</summary>
public sealed record DetectorImage(int Width, int Height, byte[] Bgra32);

/// <summary>OCR识别结果。Confidence 为空时表示引擎未提供置信度。</summary>
public sealed record OcrRecognitionResult(string Text, double? Confidence = null);

public sealed record DetectorEvaluationResult(
    string DetectorId,
    DetectorType Type,
    int State,
    bool Supported = true,
    string? Message = null,
    DateTimeOffset? EvaluatedAt = null)
{
    public bool IsOn => State == 1;
}

public sealed class DetectorEvaluationContext
{
    private readonly Dictionary<(CaptureRegion Region, bool Background, nint WindowHandle), CapturedFrame> _frames = [];
    private readonly Dictionary<string, DetectorImage> _images = new(StringComparer.OrdinalIgnoreCase);

    public required IDesktopCaptureService Capture { get; init; }
    public ScriptInteractionMode ScriptInteractionMode { get; init; } = ScriptInteractionMode.Foreground;
    public IWindowCaptureService? WindowCapture { get; init; }
    public IImageTemplateProvider? Images { get; init; }
    public IWindowAutomationService? Windows { get; init; }
    public IReadOnlyList<nint> WindowHandles { get; init; } = [];

    public Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken) =>
        CaptureAsync(region, background: false, cancellationToken);

    public async Task<CapturedFrame> CaptureAsync(
        CaptureRegion region,
        bool background,
        CancellationToken cancellationToken)
    {
        var requested = region.IsValid ? region : Capture.VirtualScreenBounds;
        var windowHandle = nint.Zero;
        if (background)
        {
            if (WindowHandles.Count != 1)
                throw new InvalidOperationException("后台读取需要唯一的目标窗口。请在脚本属性中设置窗口范围，或先使用只返回一个窗口的窗口作用域节点。");
            if (WindowCapture is null)
                throw new InvalidOperationException("后台窗口读取服务未注册，当前运行环境不支持后台读取。");
            windowHandle = WindowHandles[0];
        }

        var key = (requested, background, windowHandle);
        if (_frames.TryGetValue(key, out var cached)) return cached;
        var frame = background
            ? await WindowCapture!.CaptureAsync(windowHandle, requested, cancellationToken).ConfigureAwait(false)
            : await Capture.CaptureAsync(requested, cancellationToken).ConfigureAwait(false);
        _frames[key] = frame;
        return frame;
    }

    public async Task<DetectorImage> LoadImageAsync(string path, CancellationToken cancellationToken)
    {
        if (Images is null) throw new InvalidOperationException("未注册图片模板读取服务。");
        if (_images.TryGetValue(path, out var cached)) return cached;
        var image = await Images.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        _images[path] = image;
        return image;
    }
}
