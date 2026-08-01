using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Automation.Windows;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class ColorPickerWindow : Window
{
    private const int SampleSize = 13;
    private readonly CursorPositionService _cursor;
    private readonly IDesktopCaptureService _capture;
    private CaptureRegion _virtualScreenBounds;
    private readonly WindowBounds? _relativeTo;
    private readonly DispatcherTimer _timer;
    private bool _isUpdating;
    private bool _isClosed;
    private bool _isCompleting;
    private bool _isExcludedFromCapture;
    private string? _lastColor;

    public CoordinatePickResult? SelectedResult { get; private set; }

    public ColorPickerWindow(CursorPositionService cursor, IDesktopCaptureService capture, WindowBounds? relativeTo = null)
    {
        InitializeComponent();
        PickerWindowCaptureProtection.Attach(this, applied => _isExcludedFromCapture = applied);
        _cursor = cursor;
        _capture = capture;
        _virtualScreenBounds = capture.VirtualScreenBounds;
        _relativeTo = relativeTo;
        PhysicalOverlayWindowPlacement.Attach(
            this,
            () => capture.VirtualScreenBounds,
            bounds => _virtualScreenBounds = bounds);
        _timer = new(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, async (_, _) => await UpdatePreviewAsync(), Dispatcher);
        Loaded += async (_, _) => { Activate(); Focus(); Keyboard.Focus(this); await UpdatePreviewAsync(); _timer.Start(); };
        Closed += (_, _) => { _isClosed = true; _timer.Stop(); };
    }

    private async Task UpdatePreviewAsync()
    {
        if (_isUpdating) return;
        _isUpdating = true;
        var point = _cursor.GetPosition();
        try
        {
            var region = CreateSampleRegion(point, _virtualScreenBounds);
            PositionMagnifier(point);
            HitTestSurface.Visibility = Visibility.Hidden;
            var hideMagnifier = !_isExcludedFromCapture && MagnifierOverlaps(region);
            if (hideMagnifier) MagnifierPanel.Visibility = Visibility.Hidden;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            _ = ScreenCaptureComposition.TryFlush();
            var frame = await Task.Run(() => _capture.CaptureAsync(region, CancellationToken.None));
            var pixelX = Math.Clamp(point.X - region.X, 0, frame.Width - 1);
            var pixelY = Math.Clamp(point.Y - region.Y, 0, frame.Height - 1);
            if (!ScreenPixel.TryRead(frame, pixelX, pixelY, out var pixel))
                throw new InvalidOperationException("无法读取光标位置的像素。");
            if (_isClosed) return;

            _lastColor = pixel.Hex;
            PixelText.Text = $"{pixel.Hex}  RGB({pixel.Red}, {pixel.Green}, {pixel.Blue})";
            CoordinateText.Text = FormatCoordinate(point);
            MagnifierImage.Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra32, frame.Width * 4);
        }
        catch
        {
            if (_isClosed) return;
            _lastColor = null;
            PixelText.Text = "像素读取失败";
            CoordinateText.Text = FormatCoordinate(point);
        }
        finally
        {
            if (!_isClosed)
            {
                HitTestSurface.Visibility = Visibility.Visible;
                MagnifierPanel.Visibility = Visibility.Visible;
            }
            _isUpdating = false;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _isCompleting) return;
        _isCompleting = true;
        var point = _cursor.GetPosition();
        SelectedResult = new CoordinatePickResult(point.X, point.Y, _lastColor);
        Close();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _isCompleting) return;
        _isCompleting = true;
        SelectedResult = null;
        Close();
        e.Handled = true;
    }

    private void PositionMagnifier((int X, int Y) point)
    {
        var localPoint = PhysicalScreenCoordinateMapper.ToLocalDip(this, point.X, point.Y);
        var panelWidth = MagnifierPanel.Width;
        var panelHeight = Math.Max(MagnifierPanel.ActualHeight, 230);
        var left = localPoint.X + 22;
        var top = localPoint.Y + 22;
        if (left + panelWidth > ActualWidth - 12) left = localPoint.X - panelWidth - 22;
        if (top + panelHeight > ActualHeight - 12) top = localPoint.Y - panelHeight - 22;
        Canvas.SetLeft(MagnifierPanel, Math.Clamp(left, 8, Math.Max(8, ActualWidth - panelWidth - 8)));
        Canvas.SetTop(MagnifierPanel, Math.Clamp(top, 8, Math.Max(8, ActualHeight - panelHeight - 8)));
    }

    private string FormatCoordinate((int X, int Y) point)
    {
        var x = _relativeTo is null ? point.X : point.X - _relativeTo.Left;
        var y = _relativeTo is null ? point.Y : point.Y - _relativeTo.Top;
        return _relativeTo is null ? $"X {x}   Y {y}" : $"X {x}   Y {y}  ·  窗口相对";
    }

    private bool MagnifierOverlaps(CaptureRegion physicalSample)
    {
        var sampleTopLeft = PhysicalScreenCoordinateMapper.ToLocalDip(
            this,
            physicalSample.X,
            physicalSample.Y);
        var sampleBottomRight = PhysicalScreenCoordinateMapper.ToLocalDip(
            this,
            checked(physicalSample.X + physicalSample.Width),
            checked(physicalSample.Y + physicalSample.Height));
        var sample = new Rect(sampleTopLeft, sampleBottomRight);
        var panel = new Rect(
            Canvas.GetLeft(MagnifierPanel),
            Canvas.GetTop(MagnifierPanel),
            Math.Max(MagnifierPanel.ActualWidth, MagnifierPanel.Width),
            Math.Max(MagnifierPanel.ActualHeight, 230));
        return panel.IntersectsWith(sample);
    }

    private static CaptureRegion CreateSampleRegion((int X, int Y) point, CaptureRegion bounds)
    {
        var size = Math.Min(SampleSize, Math.Min(bounds.Width, bounds.Height));
        var half = size / 2;
        var x = Math.Clamp(point.X - half, bounds.X, bounds.X + bounds.Width - size);
        var y = Math.Clamp(point.Y - half, bounds.Y, bounds.Y + bounds.Height - size);
        return new CaptureRegion(x, y, size, size);
    }
}
