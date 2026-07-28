using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameFlowStudio.Automation.Coordinates;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class CoordinatePickerWindow : Window
{
    private readonly CursorPositionService _cursor;
    private readonly WindowBounds? _relativeTo;
    private readonly DispatcherTimer _timer;
    public (int X, int Y)? SelectedPosition { get; private set; }
    public CoordinatePickerWindow(
        CursorPositionService cursor,
        IDesktopCaptureService capture,
        WindowBounds? relativeTo = null)
    {
        InitializeComponent(); _cursor = cursor; _relativeTo = relativeTo;
        PhysicalOverlayWindowPlacement.Attach(this, () => capture.VirtualScreenBounds);
        _timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += (_, _) => { Activate(); Focus(); _timer.Start(); }; Closed += (_, _) => _timer.Stop();
    }
    private void UpdatePosition()
    {
        var point = _cursor.GetPosition();
        var localPoint = PhysicalScreenCoordinateMapper.ToLocalDip(this, point.X, point.Y);
        var displayX = _relativeTo is null ? point.X : point.X - _relativeTo.Left;
        var displayY = _relativeTo is null ? point.Y : point.Y - _relativeTo.Top;
        CoordinateText.Text = _relativeTo is null
            ? $"X {displayX}   Y {displayY}"
            : $"X {displayX}   Y {displayY}  ·  窗口相对";
        Canvas.SetLeft(CoordinateBadge, Math.Clamp(localPoint.X + 18, 0, Math.Max(0, ActualWidth - CoordinateBadge.ActualWidth)));
        Canvas.SetTop(CoordinateBadge, Math.Clamp(localPoint.Y + 18, 0, Math.Max(0, ActualHeight - CoordinateBadge.ActualHeight)));
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs e) { SelectedPosition = _cursor.GetPosition(); DialogResult = true; }
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { SelectedPosition = null; DialogResult = false; } }
}
