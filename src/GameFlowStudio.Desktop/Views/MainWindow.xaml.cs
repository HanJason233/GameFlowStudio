using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Desktop.ViewModels;

namespace GameFlowStudio.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IEditorBridge _editor;
    private bool _closePreparationStarted;
    private bool _allowClose;
    public string? StartupProjectPath { get; set; }

    public MainWindow(MainViewModel viewModel, IEditorBridge editor)
    {
        InitializeComponent(); _viewModel = viewModel; _editor = editor; DataContext = viewModel;
        Loaded += OnLoaded; Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            Width = Math.Max(MinWidth, _viewModel.Settings.WindowWidth); Height = Math.Max(MinHeight, _viewModel.Settings.WindowHeight);
            if (_viewModel.Settings.WindowLeft.HasValue && _viewModel.Settings.WindowTop.HasValue) { Left = _viewModel.Settings.WindowLeft.Value; Top = _viewModel.Settings.WindowTop.Value; }
            await _editor.InitializeAsync(EditorWebView);
            if (!string.IsNullOrWhiteSpace(StartupProjectPath))
                await _viewModel.LoadStartupProjectAsync(StartupProjectPath);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ScriptRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScriptListItem item } element) return;
        e.Handled = true;
        ShowContextMenu(
            element,
            new MenuItem { Header = "复制", Command = _viewModel.CopyScriptCommand, CommandParameter = item },
            new Separator(),
            new MenuItem { Header = "属性", Command = _viewModel.OpenScriptPropertiesCommand, CommandParameter = item },
            new MenuItem { Header = "删除", Command = _viewModel.DeleteScriptCommand, CommandParameter = item });
    }

    private void ProjectCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        e.Handled = true;
        ShowContextMenu(
            element,
            new MenuItem { Header = "复制", Command = _viewModel.CopyProjectCommand });
    }

    private void ProjectPaneRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        e.Handled = true;
        ShowContextMenu(
            element,
            new MenuItem { Header = "粘贴", Command = _viewModel.PasteProjectCommand });
    }

    private void ScriptPaneRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        e.Handled = true;
        ShowContextMenu(
            element,
            new MenuItem { Header = "粘贴", Command = _viewModel.PasteScriptCommand });
    }

    private static void ShowContextMenu(FrameworkElement placementTarget, params object[] items)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        foreach (var item in items) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closePreparationStarted || !_viewModel.CanClose()) return;

        _closePreparationStarted = true;
        IsEnabled = false;
        try
        {
            await _viewModel.PrepareForCloseAsync();
            if (Application.Current is App app)
                await app.ShutdownHostAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "关闭前保存状态失败", MessageBoxButton.OK, MessageBoxImage.Error);
            _closePreparationStarted = false;
            IsEnabled = true;
        }
    }
}
