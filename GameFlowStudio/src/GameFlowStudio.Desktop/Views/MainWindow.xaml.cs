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
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "属性", Command = _viewModel.OpenScriptPropertiesCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "删除", Command = _viewModel.DeleteScriptCommand, CommandParameter = item });
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (!_viewModel.CanClose()) e.Cancel = true; }
}
