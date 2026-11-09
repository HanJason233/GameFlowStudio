using System.IO;
using System.Runtime.InteropServices;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using OpenCvSharp;

namespace GameFlowStudio.Infrastructure.Ocr;

public sealed class OpenCvImageTemplateProvider : IImageTemplateProvider
{
    public Task<DetectorImage> LoadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("图片模板路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到图片模板。", fullPath);

        using var decoded = Cv2.ImRead(fullPath, ImreadModes.Unchanged);
        if (decoded.Empty()) throw new InvalidDataException("图片模板无法解码。");
        using var bgra = new Mat();
        switch (decoded.Channels())
        {
            case 4: decoded.CopyTo(bgra); break;
            case 3: Cv2.CvtColor(decoded, bgra, ColorConversionCodes.BGR2BGRA); break;
            case 1: Cv2.CvtColor(decoded, bgra, ColorConversionCodes.GRAY2BGRA); break;
            default: throw new InvalidDataException($"不支持 {decoded.Channels()} 通道的图片模板。");
        }
        var bytes = new byte[checked((int)(bgra.Total() * bgra.ElemSize()))];
        Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        return Task.FromResult(new DetectorImage(bgra.Width, bgra.Height, bytes));
    }
}
