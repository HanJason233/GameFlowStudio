using System.IO;
using System.Windows.Media.Imaging;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Services;

public sealed class WpfImageTemplateProvider : IImageTemplateProvider
{
    public Task<DetectorImage> LoadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("图片模板路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到图片模板。", fullPath);

        var decoder = BitmapDecoder.Create(new Uri(fullPath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return Task.FromResult(new DetectorImage(source.PixelWidth, source.PixelHeight, pixels));
    }
}
