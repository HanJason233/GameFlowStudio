using System.Windows;
using GameFlowStudio.Desktop.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class CoordinatePickModeWindow : Window
{
    public CoordinatePickMode? SelectedMode { get; private set; }

    public CoordinatePickModeWindow() => InitializeComponent();

    private void ScreenClick(object sender, RoutedEventArgs e)
    {
        SelectedMode = CoordinatePickMode.Screen;
        DialogResult = true;
    }

    private void WindowClick(object sender, RoutedEventArgs e)
    {
        SelectedMode = CoordinatePickMode.Window;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
