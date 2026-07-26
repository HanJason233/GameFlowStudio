using System.Runtime.InteropServices;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Detectors;

/// <summary>使用 GDI 把虚拟桌面复制为 BGRA32，支持负坐标的多显示器布局。</summary>
public sealed class DesktopCaptureService : IDesktopCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    public CaptureRegion VirtualScreenBounds => new(
        GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    public Task<CapturedFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = VirtualScreenBounds;
        var requested = region.IsValid ? region : bounds;
        if (requested.Width <= 0 || requested.Height <= 0) throw new InvalidOperationException("无法确定虚拟屏幕尺寸。");

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, requested.Width, requested.Height);
        if (screenDc == IntPtr.Zero || memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            ReleaseCaptureResources(screenDc, memoryDc, bitmap, IntPtr.Zero);
            throw new InvalidOperationException("无法创建屏幕捕获缓冲区。");
        }

        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, requested.Width, requested.Height, screenDc, requested.X, requested.Y, Srccopy | CaptureBlt))
                throw new InvalidOperationException("屏幕捕获失败。");

            var pixels = new byte[checked(requested.Width * requested.Height * 4)];
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
            if (GetDIBits(memoryDc, bitmap, 0, (uint)requested.Height, pixels, ref info, 0) == 0)
                throw new InvalidOperationException("无法读取屏幕像素。");
            return Task.FromResult(new CapturedFrame(requested.X, requested.Y, requested.Width, requested.Height, pixels));
        }
        finally
        {
            SelectObject(memoryDc, previous);
            ReleaseCaptureResources(screenDc, memoryDc, bitmap, previous);
        }
    }

    private static void ReleaseCaptureResources(IntPtr screenDc, IntPtr memoryDc, IntPtr bitmap, IntPtr previous)
    {
        if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero) SelectObject(memoryDc, previous);
        if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
        if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
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
