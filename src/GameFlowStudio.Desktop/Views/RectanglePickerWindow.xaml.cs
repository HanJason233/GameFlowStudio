using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class RectanglePickerWindow : Window
{
    private readonly CursorPositionService _cursor;
    private readonly WindowBounds? _relativeTo;
    private readonly DispatcherTimer _timer;
    private (int X, int Y)? _start;
    private bool _isSelecting;

    public (int X, int Y, int Width, int Height)? SelectedRegion { get; private set; }

    public RectanglePickerWindow(
        CursorPositionService cursor,
        IDesktopCaptureService capture,
        WindowBounds? relativeTo = null)
    {
        InitializeComponent();
        PhysicalOverlayWindowPlacement.Attach(this, () => capture.VirtualScreenBounds);
        _cursor = cursor;
        _relativeTo = relativeTo;
        _timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += (_, _) => { Activate(); Focus(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); if (IsMouseCaptured) ReleaseMouseCapture(); };
    }

    private void UpdatePosition()
    {
        var point = _cursor.GetPosition();
        var localPoint = PhysicalScreenCoordinateMapper.ToLocalDip(this, point.X, point.Y);
        if (_start is { } start && _isSelecting)
        {
            DrawSelection(start, point);
            var region = Normalize(start, point);
            CoordinateText.Text = FormatCoordinates(point, region);
        }
        else
        {
            CoordinateText.Text = FormatCoordinates(point, null);
        }

        Canvas.SetLeft(CoordinateBadge, Math.Clamp(localPoint.X + 18, 0, Math.Max(0, ActualWidth - CoordinateBadge.ActualWidth)));
        Canvas.SetTop(CoordinateBadge, Math.Clamp(localPoint.Y + 18, 0, Math.Max(0, ActualHeight - CoordinateBadge.ActualHeight)));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _isSelecting) return;
        _start = _cursor.GetPosition();
        _isSelecting = true;
        CaptureMouse();
        DrawSelection(_start.Value, _start.Value);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || _start is not { } start) return;
        var point = _cursor.GetPosition();
        DrawSelection(start, point);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_isSelecting || _start is not { } start) return;
        var point = _cursor.GetPosition();
        _isSelecting = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SelectedRegion = Normalize(start, point);
        DialogResult = true;
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _isSelecting = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SelectedRegion = null;
        DialogResult = false;
        e.Handled = true;
    }

    private void DrawSelection((int X, int Y) start, (int X, int Y) end)
    {
        var region = Normalize(start, end);
        var topLeft = PhysicalScreenCoordinateMapper.ToLocalDip(this, region.X, region.Y);
        var bottomRight = PhysicalScreenCoordinateMapper.ToLocalDip(
            this,
            checked(region.X + region.Width),
            checked(region.Y + region.Height));
        Canvas.SetLeft(SelectionRectangle, Math.Min(topLeft.X, bottomRight.X));
        Canvas.SetTop(SelectionRectangle, Math.Min(topLeft.Y, bottomRight.Y));
        SelectionRectangle.Width = Math.Abs(bottomRight.X - topLeft.X);
        SelectionRectangle.Height = Math.Abs(bottomRight.Y - topLeft.Y);
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private string FormatCoordinates((int X, int Y) point, (int X, int Y, int Width, int Height)? region)
    {
        var x = _relativeTo is null ? point.X : point.X - _relativeTo.Left;
        var y = _relativeTo is null ? point.Y : point.Y - _relativeTo.Top;
        var suffix = _relativeTo is null ? string.Empty : "  ·  窗口相对";
        return region is { } selected
            ? $"X {x}   Y {y}   W {selected.Width}   H {selected.Height}{suffix}"
            : $"X {x}   Y {y}{suffix}";
    }

    private static (int X, int Y, int Width, int Height) Normalize((int X, int Y) start, (int X, int Y) end) =>
        (Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(1, Math.Abs(start.X - end.X) + 1), Math.Max(1, Math.Abs(start.Y - end.Y) + 1));
}
