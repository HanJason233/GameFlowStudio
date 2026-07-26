using System.Windows;
using System.Windows.Input;
using GameFlowStudio.Contracts.Results;

namespace GameFlowStudio.Desktop.Views;

public partial class WindowSelectionWindow : Window
{
    private readonly Func<IReadOnlyList<WindowInfo>> _loadWindows;

    public WindowInfo? SelectedWindow => WindowList.SelectedItem as WindowInfo;

    public WindowSelectionWindow(Func<IReadOnlyList<WindowInfo>> loadWindows)
    {
        InitializeComponent();
        _loadWindows = loadWindows;
        RefreshWindowList();
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void RefreshWindowList()
    {
        WindowList.ItemsSource = _loadWindows().OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (WindowList.Items.Count > 0) WindowList.SelectedIndex = 0;
    }

    private void SelectClick(object sender, RoutedEventArgs e)
    {
        if (SelectedWindow is null)
        {
            MessageBox.Show("请先选择一个窗口。", "选择目标窗口", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void WindowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedWindow is not null) DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
