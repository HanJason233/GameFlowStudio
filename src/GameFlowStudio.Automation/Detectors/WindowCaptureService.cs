using System.ComponentModel;
using System.Runtime.InteropServices;
using GameFlowStudio.Automation.Interop;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Automation.Detectors;

/// <summary>
/// 请求目标窗口自行绘制到 GDI 位图，从而在不激活窗口、不依赖窗口遮挡关系的情况下读取像素。
/// 请求区域和返回帧坐标均使用虚拟桌面物理像素。
/// </summary>
public sealed class WindowCaptureService : IWindowCaptureService
{
    private static readonly nint HgdiError = new(-1);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private const uint Blackness = 0x00000042;

    public async Task<CapturedFrame> CaptureAsync(
        nint windowHandle,
        CaptureRegion region,
        CancellationToken cancellationToken)
    {
        if (windowHandle == nint.Zero)
            throw new ArgumentException("后台读取的窗口句柄不能为空。", nameof(windowHandle));
        if (!region.IsValid)
            throw new ArgumentException("后台读取区域的宽度和高度必须大于 0。", nameof(region));

        cancellationToken.ThrowIfCancellationRequested();

        // PrintWindow 是不可取消的同步调用。全局只允许一个后台抓图进入本机调用：
        // 超时后调用方可以继续停止工作流，而遗留任务结束前不会继续堆积线程和 GDI 资源。
        if (!await CaptureGate.WaitAsync(CaptureTimeout, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("后台窗口读取服务仍在等待上一次目标窗口响应，请稍后重试。");

        var capture = Task.Run(
            () => CaptureCore(windowHandle, region, cancellationToken),
            CancellationToken.None);
        try
        {
            return await capture.WaitAsync(CaptureTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"后台窗口读取超过 {CaptureTimeout.TotalSeconds:0} 秒，目标窗口可能没有响应。",
                exception);
        }
        finally
        {
            if (capture.IsCompleted)
            {
                _ = capture.Exception;
                CaptureGate.Release();
            }
            else
            {
                _ = ObserveAndReleaseAsync(capture);
            }
        }
    }

    private static async Task ObserveAndReleaseAsync(Task<CapturedFrame> capture)
    {
        try
        {
            _ = await capture.ConfigureAwait(false);
        }
        catch
        {
            // 调用方已经因超时或取消离开；这里只负责观察异常并在本机调用真正结束后释放闸门。
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private static CapturedFrame CaptureCore(
        nint windowHandle,
        CaptureRegion requested,
        CancellationToken cancellationToken)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorAware();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsWindow(windowHandle))
            throw new InvalidOperationException("后台读取的目标窗口已经失效。");
        if (!GetWindowRect(windowHandle, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取后台读取目标窗口的边界。");

        var width64 = (long)rectangle.Right - rectangle.Left;
        var height64 = (long)rectangle.Bottom - rectangle.Top;
        if (width64 is <= 0 or > int.MaxValue || height64 is <= 0 or > int.MaxValue)
            throw new InvalidOperationException("后台读取目标窗口的尺寸无效。");

        var windowRegion = new CaptureRegion(rectangle.Left, rectangle.Top, (int)width64, (int)height64);
        EnsureContained(windowRegion, requested);

        // 在创建 GDI 对象前完成容量检查，避免异常窗口尺寸触发过大的本机或托管分配。
        var fullBufferLength = DesktopCaptureService.RequiredBufferLength(windowRegion);
        _ = DesktopCaptureService.RequiredBufferLength(requested);

        var screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
            throw new InvalidOperationException("无法获取后台读取所需的桌面设备上下文。");

        var memoryDc = nint.Zero;
        var bitmap = nint.Zero;
        var previous = nint.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建后台读取设备上下文。");

            bitmap = CreateCompatibleBitmap(screenDc, windowRegion.Width, windowRegion.Height);
            if (bitmap == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建后台读取位图。");

            previous = SelectObject(memoryDc, bitmap);
            if (previous == nint.Zero || previous == HgdiError)
            {
                previous = nint.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法选择后台读取位图。");
            }

            if (!PatBlt(memoryDc, 0, 0, windowRegion.Width, windowRegion.Height, Blackness))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化后台读取位图。");

            SetLastErrorCode(0);
            if (!PrintWindow(windowHandle, memoryDc, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw error == 0
                    ? new InvalidOperationException("目标程序拒绝或不支持后台窗口读取。")
                    : new Win32Exception(error, "后台窗口读取失败。");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var deselected = SelectObject(memoryDc, previous);
            if (deselected == nint.Zero || deselected == HgdiError)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法释放后台读取位图。");
            previous = nint.Zero;

            var pixels = new byte[fullBufferLength];
            var info = new BitmapInfo
            {
                Colors = [0],
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = windowRegion.Width,
                    Height = -windowRegion.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };
            var scanLines = GetDIBits(
                screenDc,
                bitmap,
                0,
                (uint)windowRegion.Height,
                pixels,
                ref info,
                0);
            if (scanLines != windowRegion.Height)
                throw new InvalidOperationException("无法完整读取后台窗口像素。");

            // GDI 绘制后的 32 位位图 Alpha 通道未定义；检测和 OCR 需要不透明的合成结果。
            MakeOpaque(pixels);
            cancellationToken.ThrowIfCancellationRequested();

            return Crop(
                new CapturedFrame(
                    windowRegion.X,
                    windowRegion.Y,
                    windowRegion.Width,
                    windowRegion.Height,
                    pixels),
                requested);
        }
        finally
        {
            if (memoryDc != nint.Zero && previous != nint.Zero && previous != HgdiError)
                _ = SelectObject(memoryDc, previous);
            if (bitmap != nint.Zero)
                _ = DeleteObject(bitmap);
            if (memoryDc != nint.Zero)
                _ = DeleteDC(memoryDc);
            _ = ReleaseDC(nint.Zero, screenDc);
        }
    }

    internal static CapturedFrame Crop(CapturedFrame source, CaptureRegion requested)
    {
        if (!requested.IsValid)
            throw new ArgumentException("后台读取区域的宽度和高度必须大于 0。", nameof(requested));
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException("后台读取源帧的尺寸无效。", nameof(source));

        var sourceLength = DesktopCaptureService.RequiredBufferLength(
            new CaptureRegion(source.X, source.Y, source.Width, source.Height));
        if (source.Bgra32 is null || sourceLength > source.Bgra32.Length)
            throw new ArgumentException("后台读取源帧的 BGRA32 像素不完整。", nameof(source));

        var sourceRegion = new CaptureRegion(source.X, source.Y, source.Width, source.Height);
        EnsureContained(sourceRegion, requested);
        if (requested == sourceRegion)
            return source;

        var destination = new byte[DesktopCaptureService.RequiredBufferLength(requested)];
        var offsetX = checked(requested.X - source.X);
        var offsetY = checked(requested.Y - source.Y);
        var sourceStride = checked(source.Width * 4);
        var destinationStride = checked(requested.Width * 4);

        for (var row = 0; row < requested.Height; row++)
        {
            var sourceOffset = checked((offsetY + row) * sourceStride + offsetX * 4);
            var destinationOffset = checked(row * destinationStride);
            Buffer.BlockCopy(source.Bgra32, sourceOffset, destination, destinationOffset, destinationStride);
        }

        return new CapturedFrame(
            requested.X,
            requested.Y,
            requested.Width,
            requested.Height,
            destination);
    }

    internal static void MakeOpaque(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("BGRA32 像素缓冲区长度必须是 4 的倍数。", nameof(pixels));

        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = byte.MaxValue;
    }

    private static void EnsureContained(CaptureRegion container, CaptureRegion requested)
    {
        var containerRight = (long)container.X + container.Width;
        var containerBottom = (long)container.Y + container.Height;
        var requestedRight = (long)requested.X + requested.Width;
        var requestedBottom = (long)requested.Y + requested.Height;

        if (requested.X < container.X
            || requested.Y < container.Y
            || requestedRight > containerRight
            || requestedBottom > containerBottom)
        {
            throw new InvalidOperationException(
                "后台读取区域必须完整位于目标窗口内，不能读取窗口外部。");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint deviceContext, nint graphicObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PatBlt(
        nint deviceContext,
        int x,
        int y,
        int width,
        int height,
        uint operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo info,
        uint usage);

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    private static extern void SetLastErrorCode(uint errorCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public int[] Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
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
