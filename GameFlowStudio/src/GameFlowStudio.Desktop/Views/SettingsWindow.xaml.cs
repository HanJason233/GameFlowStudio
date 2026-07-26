using System.Windows;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings) { InitializeComponent(); DataContext = settings; }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var settings = (AppSettings)DataContext;
        var keys = new[] { settings.RunHotkey, settings.PauseHotkey, settings.StopHotkey };
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) { MessageBox.Show("三个快捷键不能重复。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (settings.StepCategoryLayout is not ("vertical" or "horizontal")) settings.StepCategoryLayout = "vertical";
        DialogResult = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
