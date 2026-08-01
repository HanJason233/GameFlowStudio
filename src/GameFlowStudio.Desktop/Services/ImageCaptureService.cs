using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Storage.Serialization;

namespace GameFlowStudio.Desktop.Services;

public interface IImageCaptureService
{
    string CreatePrivateTemplatePath();
    Task SaveAsync(CaptureRegion region, string path, CancellationToken cancellationToken = default);
    Task SaveAsync(CapturedFrame frame, string path, CancellationToken cancellationToken = default);
}

public sealed class ImageCaptureService(IDesktopCaptureService capture) : IImageCaptureService
{
    public string CreatePrivateTemplatePath()
    {
        Directory.CreateDirectory(StoragePaths.ImageTemplatesDirectory);
        return Path.Combine(StoragePaths.ImageTemplatesDirectory, $"template-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
    }

    public async Task SaveAsync(CaptureRegion region, string path, CancellationToken cancellationToken = default)
    {
        if (!region.IsValid) throw new ArgumentException("截图区域无效。", nameof(region));
        ValidatePath(path);

        var frame = await capture.CaptureAsync(region, cancellationToken).ConfigureAwait(true);
        await SaveAsync(frame, path, cancellationToken).ConfigureAwait(true);
    }

    public async Task SaveAsync(CapturedFrame frame, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Width > int.MaxValue / 4 || frame.Height <= 0)
            throw new ArgumentException("截图帧无效。", nameof(frame));
        var stride = checked(frame.Width * 4);
        if (frame.Bgra32 is null || frame.Bgra32.LongLength < (long)stride * frame.Height)
            throw new ArgumentException("截图帧像素数据不完整。", nameof(frame));
        ValidatePath(path);

        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra32, stride);
        bitmap.Freeze();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("截图路径不能为空。", nameof(path));
    }
}
