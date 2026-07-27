using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class ColorPickerWindow : Window
{
    private const int SampleSize = 13;
    private readonly CursorPositionService _cursor;
    private readonly IDesktopCaptureService _capture;
    private readonly WindowBounds? _relativeTo;
    private readonly DispatcherTimer _timer;
    private bool _isUpdating;
    private bool _isClosed;
    private string? _lastColor;

    public CoordinatePickResult? SelectedResult { get; private set; }

    public ColorPickerWindow(CursorPositionService cursor, IDesktopCaptureService capture, WindowBounds? relativeTo = null)
    {
        InitializeComponent();
        _cursor = cursor;
        _capture = capture;
        _relativeTo = relativeTo;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _timer = new(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, async (_, _) => await UpdatePreviewAsync(), Dispatcher);
        Loaded += async (_, _) => { Activate(); Focus(); await UpdatePreviewAsync(); _timer.Start(); };
        Closed += (_, _) => { _isClosed = true; _timer.Stop(); };
    }

    private async Task UpdatePreviewAsync()
    {
        if (_isUpdating) return;
        _isUpdating = true;
        var point = _cursor.GetPosition();
        try
        {
            var region = CreateSampleRegion(point, _capture.VirtualScreenBounds);
            PositionMagnifier(point);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var frame = await Task.Run(() => _capture.CaptureAsync(region, CancellationToken.None));
            var pixelX = Math.Clamp(point.X - region.X, 0, frame.Width - 1);
            var pixelY = Math.Clamp(point.Y - region.Y, 0, frame.Height - 1);
            var pixel = frame.Pixel(pixelX, pixelY);
            if (pixel.Length < 3) throw new InvalidOperationException("无法读取光标位置的像素。");
            if (_isClosed) return;

            _lastColor = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";
            PixelText.Text = $"{_lastColor}  RGB({pixel[2]}, {pixel[1]}, {pixel[0]})";
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
            _isUpdating = false;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var point = _cursor.GetPosition();
        SelectedResult = new CoordinatePickResult(point.X, point.Y, _lastColor);
        DialogResult = true;
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SelectedResult = null;
        DialogResult = false;
        e.Handled = true;
    }

    private void PositionMagnifier((int X, int Y) point)
    {
        var panelWidth = MagnifierPanel.Width;
        var panelHeight = Math.Max(MagnifierPanel.ActualHeight, 230);
        var left = point.X - Left + 22;
        var top = point.Y - Top + 22;
        if (left + panelWidth > Width - 12) left = point.X - Left - panelWidth - 22;
        if (top + panelHeight > Height - 12) top = point.Y - Top - panelHeight - 22;
        Canvas.SetLeft(MagnifierPanel, Math.Clamp(left, 8, Math.Max(8, Width - panelWidth - 8)));
        Canvas.SetTop(MagnifierPanel, Math.Clamp(top, 8, Math.Max(8, Height - panelHeight - 8)));
    }

    private string FormatCoordinate((int X, int Y) point)
    {
        var x = _relativeTo is null ? point.X : point.X - _relativeTo.Left;
        var y = _relativeTo is null ? point.Y : point.Y - _relativeTo.Top;
        return _relativeTo is null ? $"X {x}   Y {y}" : $"X {x}   Y {y}  ·  窗口相对";
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
