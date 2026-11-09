using System.Runtime.InteropServices;
using GameFlowStudio.Desktop.Interaction.Interop;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Interaction;

/// <summary>使用 GDI 把虚拟桌面复制为 BGRA32，支持负坐标的多显示器布局。</summary>
public sealed class DesktopCaptureService : IDesktopCaptureService
{
    private static readonly nint HgdiError = new(-1);
    private const long MaxCaptureBufferBytes = 512L * 1024 * 1024;
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    /// <summary>虚拟桌面边界，单位始终为屏幕物理像素。</summary>
    public CaptureRegion VirtualScreenBounds => PhysicalDesktopBounds.GetCurrent();

    public Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        cancellationToken.ThrowIfCancellationRequested();
        var requested = region.IsValid ? region : VirtualScreenBounds;
        if (requested.Width <= 0 || requested.Height <= 0) throw new InvalidOperationException("无法确定虚拟屏幕尺寸。");
        var bufferLength = RequiredBufferLength(requested);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("无法获取桌面设备上下文。");

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = memoryDc == IntPtr.Zero
            ? IntPtr.Zero
            : CreateCompatibleBitmap(screenDc, requested.Width, requested.Height);
        if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            ReleaseCaptureResources(screenDc, memoryDc, bitmap, IntPtr.Zero);
            throw new InvalidOperationException("无法创建屏幕捕获缓冲区。");
        }

        var previous = SelectObject(memoryDc, bitmap);
        if (previous == IntPtr.Zero || previous == HgdiError)
        {
            ReleaseCaptureResources(screenDc, memoryDc, bitmap, IntPtr.Zero);
            throw new InvalidOperationException("无法选择屏幕捕获位图。");
        }

        try
        {
            if (!BitBlt(memoryDc, 0, 0, requested.Width, requested.Height, screenDc, requested.X, requested.Y, Srccopy | CaptureBlt))
                throw new InvalidOperationException("屏幕捕获失败。");

            var selectedBitmap = SelectObject(memoryDc, previous);
            if (selectedBitmap == IntPtr.Zero || selectedBitmap == HgdiError)
                throw new InvalidOperationException("无法从屏幕捕获设备上下文释放位图。");
            previous = IntPtr.Zero;

            var pixels = new byte[bufferLength];
            var info = new BitmapInfo
            {
                Colors = [0],
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = requested.Width,
                    Height = -requested.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };
            if (GetDIBits(screenDc, bitmap, 0, (uint)requested.Height, pixels, ref info, 0) == 0)
                throw new InvalidOperationException("无法读取屏幕像素。");
            return Task.FromResult(new CapturedFrame(requested.X, requested.Y, requested.Width, requested.Height, pixels));
        }
        finally
        {
            ReleaseCaptureResources(screenDc, memoryDc, bitmap, previous);
        }
    }

    internal static int RequiredBufferLength(CaptureRegion region)
    {
        if (!region.IsValid)
            throw new ArgumentException("截图区域无效。", nameof(region));

        var pixels = (long)region.Width * region.Height;
        if (pixels > MaxCaptureBufferBytes / 4)
            throw new InvalidOperationException("截图区域过大，像素缓冲区不能超过 512 MiB。");
        return checked((int)(pixels * 4));
    }

    private static void ReleaseCaptureResources(IntPtr screenDc, IntPtr memoryDc, IntPtr bitmap, IntPtr previous)
    {
        if (memoryDc != IntPtr.Zero)
        {
            if (previous != IntPtr.Zero && previous != HgdiError)
                _ = SelectObject(memoryDc, previous);
            _ = DeleteDC(memoryDc);
        }
        if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
        if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr objectHandle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr objectHandle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint startScan, uint scanLines, [Out] byte[] bits, ref BitmapInfo info, uint usage);

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public int[] Colors;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}

