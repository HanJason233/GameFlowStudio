using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Services;

/// <summary>使用 Windows 自带 OCR 语言包识别截图中的文本。</summary>
public sealed class WindowsOcrService : IOcrService
{
    public async Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra32.Length < checked(frame.Width * frame.Height * 4))
            throw new InvalidOperationException("OCR 输入图像无效。");

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new InvalidOperationException("系统未安装可用的 Windows OCR 语言包。");

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            frame.Bgra32.AsBuffer(), BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return new OcrRecognitionResult(result.Text);
    }
}
