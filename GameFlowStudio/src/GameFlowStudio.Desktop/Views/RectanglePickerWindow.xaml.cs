using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Desktop.Views;

public partial class RectanglePickerWindow : Window
{
    private readonly CursorPositionService _cursor;
    private readonly WindowBounds? _relativeTo;
    private readonly DispatcherTimer _timer;
    private (int X, int Y)? _start;
    private bool _isSelecting;

    public (int X, int Y, int Width, int Height)? SelectedRegion { get; private set; }

    public RectanglePickerWindow(CursorPositionService cursor, WindowBounds? relativeTo = null)
    {
        InitializeComponent();
        _cursor = cursor;
        _relativeTo = relativeTo;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += (_, _) => { Activate(); Focus(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); if (IsMouseCaptured) ReleaseMouseCapture(); };
    }

    private void UpdatePosition()
    {
        var point = _cursor.GetPosition();
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

        Canvas.SetLeft(CoordinateBadge, Math.Clamp(point.X - Left + 18, 0, Math.Max(0, Width - 210)));
        Canvas.SetTop(CoordinateBadge, Math.Clamp(point.Y - Top + 18, 0, Math.Max(0, Height - 52)));
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
        Canvas.SetLeft(SelectionRectangle, region.X - Left);
        Canvas.SetTop(SelectionRectangle, region.Y - Top);
        SelectionRectangle.Width = region.Width;
        SelectionRectangle.Height = region.Height;
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
