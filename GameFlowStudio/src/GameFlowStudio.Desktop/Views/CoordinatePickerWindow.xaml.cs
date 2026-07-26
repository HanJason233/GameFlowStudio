using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameFlowStudio.Automation.Coordinates;

namespace GameFlowStudio.Desktop.Views;

public partial class CoordinatePickerWindow : Window
{
    private readonly CursorPositionService _cursor;
    private readonly DispatcherTimer _timer;
    public (int X, int Y)? SelectedPosition { get; private set; }
    public CoordinatePickerWindow(CursorPositionService cursor)
    {
        InitializeComponent(); _cursor = cursor;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop; Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        _timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += (_, _) => { Activate(); Focus(); _timer.Start(); }; Closed += (_, _) => _timer.Stop();
    }
    private void UpdatePosition()
    {
        var point = _cursor.GetPosition(); CoordinateText.Text = $"X {point.X}   Y {point.Y}";
        Canvas.SetLeft(CoordinateBadge, Math.Clamp(point.X - Left + 18, 0, Math.Max(0, Width - 170)));
        Canvas.SetTop(CoordinateBadge, Math.Clamp(point.Y - Top + 18, 0, Math.Max(0, Height - 50)));
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs e) { SelectedPosition = _cursor.GetPosition(); DialogResult = true; }
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { SelectedPosition = null; DialogResult = false; } }
}
