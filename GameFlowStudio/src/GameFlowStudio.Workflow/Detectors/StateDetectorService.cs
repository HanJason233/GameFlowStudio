using System.Globalization;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Workflow.Coordinates;
using OpenCvSharp;

namespace GameFlowStudio.Workflow.Detectors;

public sealed class StateDetectorService(IEnumerable<IStateDetector> detectors) : IStateDetectorService
{
    private readonly Dictionary<DetectorType, IStateDetector> _detectors =
        detectors.GroupBy(detector => detector.Type).ToDictionary(group => group.Key, group => group.First());

    public async Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken)
    {
        if (!detector.Enabled)
            return Result(detector, 0, false, "检测器已禁用。");
        if (!_detectors.TryGetValue(detector.Type, out var implementation))
            return Result(detector, 0, false, $"未注册检测器类型 {detector.Type}。");

        try { return await implementation.EvaluateAsync(detector, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return Result(detector, 0, false, exception.Message); }
    }

    internal static DetectorEvaluationResult Result(DetectorDefinition detector, int state, bool supported = true, string? message = null) =>
        new(detector.Id, detector.Type, state is 1 ? 1 : 0, supported, message, DateTimeOffset.UtcNow);
}

public sealed class ColorStateDetector : IStateDetector
{
    public DetectorType Type => DetectorType.Color;

    public async Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken)
    {
        var origin = CoordinateOrigin.Read(detector, context);
        var region = ReadRegion(detector, context.Capture.VirtualScreenBounds, origin, defaultWidth: 1, defaultHeight: 1);
        var target = ParseColor(DetectorParameterReader.String(detector, "targetColor", "#FFFFFF"));
        var tolerance = Math.Clamp(DetectorParameterReader.Int(detector, "tolerance", 10), 0, 255);
        var minimumRatio = Math.Clamp(DetectorParameterReader.Double(detector, "minMatchRatio", 1), 0, 1);
        var frame = await context.CaptureAsync(
            region,
            DetectorCaptureModeReader.IsBackground(detector),
            cancellationToken).ConfigureAwait(false);
        var total = frame.Width * frame.Height;
        var matching = 0;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var pixel = frame.Pixel(x, y);
            if (pixel.Length == 4 && Math.Abs(pixel[2] - target.R) <= tolerance && Math.Abs(pixel[1] - target.G) <= tolerance && Math.Abs(pixel[0] - target.B) <= tolerance)
                matching++;
        }

        var ratio = total == 0 ? 0 : (double)matching / total;
        var state = ratio >= minimumRatio ? 1 : 0;
        var sample = frame.Pixel(0, 0);
        var sampledColor = sample.Length == 4 ? $"#{sample[2]:X2}{sample[1]:X2}{sample[0]:X2}" : "无有效像素";
        var targetColor = $"#{target.R:X2}{target.G:X2}{target.B:X2}";
        return StateDetectorService.Result(detector, state, true,
            $"实际色值（首像素）{sampledColor}；目标色值 {targetColor}；容差 ±{tolerance}；匹配比例 {ratio:P1} / 要求 {minimumRatio:P1}；结果 {DetectorLogFormatting.Outcome(state == 1)}。");
    }

    private static CaptureRegion ReadRegion(DetectorDefinition detector, CaptureRegion fallback, CoordinateOrigin origin, int defaultWidth, int defaultHeight)
    {
        var x = DetectorParameterReader.Int(detector, "x", origin.IsWindow ? 0 : fallback.X);
        var y = DetectorParameterReader.Int(detector, "y", origin.IsWindow ? 0 : fallback.Y);
        var width = DetectorParameterReader.Int(detector, "width", defaultWidth);
        var height = DetectorParameterReader.Int(detector, "height", defaultHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("检测区域的宽度和高度必须大于 0。");
        return new(x + origin.X, y + origin.Y, width, height);
    }

    private static (byte R, byte G, byte B) ParseColor(string value)
    {
        var normalized = value.Trim().TrimStart('#');
        if (normalized.Length == 3) normalized = string.Concat(normalized.Select(character => $"{character}{character}"));
        if (normalized.Length != 6 || !int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
            throw new InvalidOperationException($"颜色值 {value} 无效，应使用 #RRGGBB 格式。");
        return ((byte)(number >> 16), (byte)(number >> 8), (byte)number);
    }
}

public sealed class ImageStateDetector : IStateDetector
{
    public DetectorType Type => DetectorType.Image;

    public async Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken)
    {
        var templatePath = DetectorParameterReader.String(detector, "templatePath");
        var template = await context.LoadImageAsync(templatePath, cancellationToken).ConfigureAwait(false);
        if (template.Width <= 0 || template.Height <= 0 || template.Bgra32.Length < template.Width * template.Height * 4)
            throw new InvalidOperationException("图片模板为空或不是 BGRA32 图片。");

        var searchMode = DetectorParameterReader.String(detector, "imageSearchMode", "fixed").Trim().ToLowerInvariant();
        if (searchMode is not ("fixed" or "current"))
            throw new InvalidOperationException("图片匹配范围只支持固定坐标或当前屏幕/窗口。");
        var search = searchMode == "current"
            ? DetectorSearchRegionReader.ReadCurrent(context)
            : ReadSearchRegion(detector, context.Capture.VirtualScreenBounds, CoordinateOrigin.Read(detector, context));
        var frame = await context.CaptureAsync(
            search,
            DetectorCaptureModeReader.IsBackground(detector),
            cancellationToken).ConfigureAwait(false);
        var tolerance = Math.Clamp(DetectorParameterReader.Int(detector, "tolerance", 10), 0, 255);
        var threshold = Math.Clamp(DetectorParameterReader.Double(detector, "matchThreshold", 0.95), 0, 1);
        var engine = DetectorParameterReader.String(detector, "imageMatchEngine", "custom").Trim().ToLowerInvariant();
        var best = engine switch
        {
            "custom" or "" => FindBestMatch(frame, template, tolerance, cancellationToken),
            "opencv" => FindBestMatchWithOpenCv(frame, template, cancellationToken),
            _ => throw new InvalidOperationException("图像匹配引擎只支持自实现或 OpenCV。")
        };
        var state = best >= threshold ? 1 : 0;
        return StateDetectorService.Result(detector, state, true,
            $"最佳匹配置信度 {best:P1} / 要求 {threshold:P1}；容差 ±{tolerance}；匹配范围 {(searchMode == "current" ? "当前操作范围" : "固定坐标区域")}；搜索区域 ({search.X},{search.Y},{search.Width}×{search.Height})；结果 {DetectorLogFormatting.Outcome(state == 1)}。");
    }

    private static CaptureRegion ReadSearchRegion(DetectorDefinition detector, CaptureRegion fallback, CoordinateOrigin origin)
    {
        var hasWidth = detector.Parameters.ContainsKey("width");
        var hasHeight = detector.Parameters.ContainsKey("height");
        var width = DetectorParameterReader.Int(detector, "width", origin.Width ?? fallback.Width);
        var height = DetectorParameterReader.Int(detector, "height", origin.Height ?? fallback.Height);
        if (!hasWidth || !hasHeight) { width = origin.Width ?? fallback.Width; height = origin.Height ?? fallback.Height; }
        if (width <= 0 || height <= 0) throw new InvalidOperationException("图片搜索区域的宽度和高度必须大于 0。");
        return new(
            DetectorParameterReader.Int(detector, "x", origin.IsWindow ? 0 : fallback.X) + origin.X,
            DetectorParameterReader.Int(detector, "y", origin.IsWindow ? 0 : fallback.Y) + origin.Y, width, height);
    }

    private static double FindBestMatch(CapturedFrame frame, DetectorImage template, int tolerance, CancellationToken cancellationToken)
    {
        if (template.Width > frame.Width || template.Height > frame.Height) return 0;
        var best = 0d;
        var maxX = frame.Width - template.Width;
        var maxY = frame.Height - template.Height;
        for (var y = 0; y <= maxY; y++)
        for (var x = 0; x <= maxX; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matching = 0;
            for (var templateY = 0; templateY < template.Height; templateY++)
            for (var templateX = 0; templateX < template.Width; templateX++)
            {
                var source = frame.Pixel(x + templateX, y + templateY);
                var offset = (templateY * template.Width + templateX) * 4;
                if (Math.Abs(source[0] - template.Bgra32[offset]) <= tolerance &&
                    Math.Abs(source[1] - template.Bgra32[offset + 1]) <= tolerance &&
                    Math.Abs(source[2] - template.Bgra32[offset + 2]) <= tolerance)
                    matching++;
            }
            best = Math.Max(best, (double)matching / (template.Width * template.Height));
            if (best >= 1) return best;
        }
        return best;
    }

    private static double FindBestMatchWithOpenCv(CapturedFrame frame, DetectorImage template, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (template.Width > frame.Width || template.Height > frame.Height) return 0;

        using var frameBgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra32);
        using var templateBgra = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra32);
        using var frameBgr = new Mat();
        using var templateBgr = new Mat();
        using var result = new Mat();
        Cv2.CvtColor(frameBgra, frameBgr, ColorConversionCodes.BGRA2BGR);
        Cv2.CvtColor(templateBgra, templateBgr, ColorConversionCodes.BGRA2BGR);
        Cv2.MatchTemplate(frameBgr, templateBgr, result, TemplateMatchModes.SqDiffNormed);
        Cv2.MinMaxLoc(result, out var minimum, out _, out _, out _);
        return Math.Clamp(1d - minimum, 0, 1);
    }
}

internal static class DetectorSearchRegionReader
{
    public static CaptureRegion ReadCurrent(DetectorEvaluationContext context)
    {
        if (context.WindowHandles.Count == 0) return context.Capture.VirtualScreenBounds;
        if (context.Windows is null) throw new InvalidOperationException("当前窗口搜索未注册窗口服务。");
        if (context.WindowHandles.Count != 1) throw new InvalidOperationException("当前窗口搜索需要唯一的当前操作窗口。");

        var bounds = context.Windows.GetWindowBounds(context.WindowHandles[0]);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("当前操作窗口的尺寸无效。");
        return new(bounds.Left, bounds.Top, width, height);
    }
}

internal static class DetectorCaptureModeReader
{
    public static bool IsBackground(DetectorDefinition detector)
    {
        var value = DetectorParameterReader.String(detector, "captureMode", "foreground").Trim();
        return value.ToLowerInvariant() switch
        {
            "" or "foreground" => false,
            "background" => true,
            _ => throw new InvalidOperationException(
                $"参数 captureMode 只支持 foreground 或 background，当前值为「{value}」。")
        };
    }
}

/// <summary>在屏幕区域执行 OCR，并按识别文本及置信度输出状态。</summary>
public sealed class OcrStateDetector(IOcrService geminiOcr, ILocalOcrService? localOcr = null) : IStateDetector
{
    public DetectorType Type => DetectorType.Ocr;

    public async Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken)
    {
        var targetText = ReadText(detector).Trim();
        if (targetText.Length == 0) throw new InvalidOperationException("匹配文本不能为空。");

        var searchMode = DetectorParameterReader.String(detector, "textSearchMode", "fixed").Trim().ToLowerInvariant();
        if (searchMode is not ("fixed" or "current"))
            throw new InvalidOperationException("文本匹配范围只支持固定坐标或当前屏幕/窗口。");
        var region = searchMode == "current"
            ? DetectorSearchRegionReader.ReadCurrent(context)
            : ReadRegion(detector, context.Capture.VirtualScreenBounds, CoordinateOrigin.Read(detector, context));
        var frame = await context.CaptureAsync(
            region,
            DetectorCaptureModeReader.IsBackground(detector),
            cancellationToken).ConfigureAwait(false);
        var engine = DetectorParameterReader.String(detector, "ocrEngine", "gemini").Trim().ToLowerInvariant();
        var selectedOcr = engine switch
        {
            "gemini" or "" => geminiOcr,
            "local" when localOcr is not null => localOcr,
            "local" => throw new InvalidOperationException("本地 OCR 服务未注册。"),
            _ => throw new InvalidOperationException("文本识别引擎只支持本地 OCR 或 Gemini OCR。")
        };
        var recognition = selectedOcr is ITargetAwareOcrService targetAware
            ? await targetAware.RecognizeAsync(frame, targetText, cancellationToken).ConfigureAwait(false)
            : await selectedOcr.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        var normalizedTarget = Normalize(targetText);
        var normalizedText = Normalize(recognition.Text);
        var threshold = Math.Clamp(ReadThreshold(detector), 0, 1);
        var textScore = Similarity(normalizedTarget, normalizedText);
        var score = recognition.Confidence is { } engineConfidence ? Math.Min(textScore, Math.Clamp(engineConfidence, 0, 1)) : textScore;
        var state = score >= threshold ? 1 : 0;
        var confidence = recognition.Confidence is { } reportedConfidence ? $"，引擎置信度 {reportedConfidence:P1}" : string.Empty;
        return StateDetectorService.Result(detector, state, true,
            $"识别文本「{DetectorLogFormatting.DisplayText(recognition.Text)}」；目标文本「{DetectorLogFormatting.DisplayText(targetText)}」；匹配得分 {score:P1} / 要求 {threshold:P1}{confidence}；匹配范围 {(searchMode == "current" ? "当前操作范围" : "固定坐标区域")}；识别区域 ({region.X},{region.Y},{region.Width}×{region.Height})；结果 {DetectorLogFormatting.Outcome(state == 1)}。");
    }

    private static CaptureRegion ReadRegion(DetectorDefinition detector, CaptureRegion fallback, CoordinateOrigin origin)
    {
        var x = DetectorParameterReader.Int(detector, "x", origin.IsWindow ? 0 : fallback.X);
        var y = DetectorParameterReader.Int(detector, "y", origin.IsWindow ? 0 : fallback.Y);
        var width = DetectorParameterReader.Int(detector, "width", origin.Width ?? fallback.Width);
        var height = DetectorParameterReader.Int(detector, "height", origin.Height ?? fallback.Height);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("文本识别区域的宽度和高度必须大于 0。");
        return new(x + origin.X, y + origin.Y, width, height);
    }

    private static string Normalize(string value) =>
        new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();

    private static double Similarity(string target, string actual)
    {
        if (target.Length == 0 || actual.Length == 0) return 0;
        if (actual.Contains(target, StringComparison.Ordinal)) return 1;
        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];
        for (var column = 0; column <= actual.Length; column++) previous[column] = column;
        for (var row = 1; row <= target.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= actual.Length; column++)
            {
                var substitution = previous[column - 1] + (target[row - 1] == actual[column - 1] ? 0 : 1);
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return 1d - (double)previous[actual.Length] / Math.Max(target.Length, actual.Length);
    }

    private static string ReadText(DetectorDefinition detector) =>
        detector.Parameters.ContainsKey("targetText")
            ? DetectorParameterReader.String(detector, "targetText")
            : DetectorParameterReader.String(detector, "expectedText");

    private static double ReadThreshold(DetectorDefinition detector) =>
        detector.Parameters.ContainsKey("matchThreshold")
            ? DetectorParameterReader.Double(detector, "matchThreshold", 0.8)
            : DetectorParameterReader.Double(detector, "confidenceThreshold", 0.8);
}

internal static class DetectorLogFormatting
{
    public static string Outcome(bool matched) => matched ? "匹配" : "不匹配";

    public static string DisplayText(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal sealed record CoordinateOrigin(bool IsWindow, int X, int Y, int? Width = null, int? Height = null)
{
    public static CoordinateOrigin Read(DetectorDefinition detector, DetectorEvaluationContext context)
    {
        var mode = CoordinateModeReader.Read(DetectorParameterReader.String(detector, "coordinateMode", "followScript"));
        if (!CoordinateModeReader.UsesWindow(mode, context.WindowHandles)) return new(false, 0, 0);
        if (context.Windows is null) throw new InvalidOperationException("相对坐标未注册窗口服务。");
        var bounds = CoordinateModeReader.GetWindowBounds(context.WindowHandles, context.Windows);
        return new(true, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
}

public sealed class TodoStateDetector(DetectorType type) : IStateDetector
{
    public DetectorType Type { get; } = type;

    public Task<DetectorEvaluationResult> EvaluateAsync(DetectorDefinition detector, DetectorEvaluationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(StateDetectorService.Result(detector, 0, false, $"{Type} 检测器 TODO：当前版本尚未实现。"));
}
