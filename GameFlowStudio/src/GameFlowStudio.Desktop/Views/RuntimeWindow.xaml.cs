using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Desktop.ViewModels;

namespace GameFlowStudio.Desktop.Views;

public partial class RuntimeWindow : Window
{
    private readonly RuntimeViewModel _viewModel;
    private string? _projectPath;
    private bool _isClosing;

    public RuntimeWindow(RuntimeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public async Task InitializeAsync(RuntimeLaunchOptions options, CancellationToken cancellationToken = default)
    {
        _projectPath = options.ProjectPath;
        await _viewModel.InitializeAsync(options.ProjectPath, options.ScriptId, cancellationToken);
    }

    private void TitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing || !_viewModel.IsWorkflowActive) return;
        e.Cancel = true;
        _isClosing = true;
        await _viewModel.StopForCloseAsync();
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
        if (_projectPath is not null)
        {
            try { File.Delete(_projectPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
