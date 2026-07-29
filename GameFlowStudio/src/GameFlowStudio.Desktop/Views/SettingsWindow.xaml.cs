using System.Windows;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        DataContext = settings;
        GeminiApiKeyBox.Password = settings.GeminiApiKey;
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var settings = (AppSettings)DataContext;
        settings.GeminiApiKey = GeminiApiKeyBox.Password.Trim();
        if (settings.StepCategoryLayout is not ("vertical" or "horizontal")) settings.StepCategoryLayout = "vertical";
        DialogResult = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
