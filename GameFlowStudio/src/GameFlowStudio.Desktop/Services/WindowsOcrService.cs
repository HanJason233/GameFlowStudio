using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Services;

/// <summary>使用 Windows 自带 OCR 语言包识别截图中的文本。</summary>
public sealed class WindowsOcrService : IOcrService, ITargetAwareOcrService
{
    private const int UpscaleFactor = 2;
    private const int MaxBitmapDimension = 4096;

    public async Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
        => await RecognizeInternalAsync(frame, null, cancellationToken).ConfigureAwait(false);

    public async Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, string targetText, CancellationToken cancellationToken)
        => await RecognizeInternalAsync(frame, targetText, cancellationToken).ConfigureAwait(false);

    private async Task<OcrRecognitionResult> RecognizeInternalAsync(CapturedFrame frame, string? targetText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra32.Length < checked(frame.Width * frame.Height * 4))
            throw new InvalidOperationException("OCR 输入图像无效。");

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new InvalidOperationException("系统未安装可用的 Windows OCR 语言包。");

        var original = await RecognizeBitmapAsync(engine, frame.Width, frame.Height, frame.Bgra32, cancellationToken).ConfigureAwait(false);
        var prepared = PrepareFrame(frame, cancellationToken);
        var enhanced = await RecognizeBitmapAsync(engine, prepared.Width, prepared.Height, prepared.Bgra32, cancellationToken).ConfigureAwait(false);
        return SelectResult(original, enhanced, targetText);
    }

    private static async Task<OcrRecognitionResult> RecognizeBitmapAsync(OcrEngine engine, int width, int height, byte[] pixels, CancellationToken cancellationToken)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return new OcrRecognitionResult(result.Text);
    }

    private static OcrRecognitionResult SelectResult(OcrRecognitionResult original, OcrRecognitionResult enhanced, string? targetText)
    {
        if (string.IsNullOrWhiteSpace(original.Text)) return enhanced;
        if (string.IsNullOrWhiteSpace(enhanced.Text)) return original;
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            var target = Normalize(targetText);
            var originalMatched = Normalize(original.Text).Contains(target, StringComparison.Ordinal);
            var enhancedMatched = Normalize(enhanced.Text).Contains(target, StringComparison.Ordinal);
            if (enhancedMatched != originalMatched) return enhancedMatched ? enhanced : original;
        }

        return TextQuality(enhanced.Text) >= TextQuality(original.Text) ? enhanced : original;
    }

    private static int TextQuality(string text) =>
        text.Count(character => char.IsLetterOrDigit(character) || IsCjk(character)) * 10 + Math.Min(text.Trim().Length, 100);

    private static bool IsCjk(char character) => character is >= '\u3400' and <= '\u9FFF';

    private static string Normalize(string value) =>
        new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();

    private static PreparedFrame PrepareFrame(CapturedFrame frame, CancellationToken cancellationToken)
    {
        var scale = frame.Width <= MaxBitmapDimension / UpscaleFactor && frame.Height <= MaxBitmapDimension / UpscaleFactor
            ? UpscaleFactor
            : 1;
        var width = checked(frame.Width * scale);
        var height = checked(frame.Height * scale);
        var pixels = new byte[checked(width * height * 4)];

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = y / scale;
            for (var x = 0; x < width; x++)
            {
                var sourceX = x / scale;
                var sourceOffset = (sourceY * frame.Width + sourceX) * 4;
                var targetOffset = (y * width + x) * 4;
                var blue = frame.Bgra32[sourceOffset];
                var green = frame.Bgra32[sourceOffset + 1];
                var red = frame.Bgra32[sourceOffset + 2];
                var luminance = (byte)Math.Clamp((red * 299 + green * 587 + blue * 114) / 1000, 0, 255);
                var contrast = (byte)Math.Clamp((luminance - 128) * 1.35 + 128, 0, 255);
                pixels[targetOffset] = contrast;
                pixels[targetOffset + 1] = contrast;
                pixels[targetOffset + 2] = contrast;
                pixels[targetOffset + 3] = 255;
            }
        }

        return new(width, height, pixels);
    }

    private sealed record PreparedFrame(int Width, int Height, byte[] Bgra32);
}
