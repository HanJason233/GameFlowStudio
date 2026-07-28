using System.ComponentModel;
using System.Diagnostics;
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
    private bool _keepProjectFile;

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
        if (e.ChangedButton != MouseButton.Left) return;

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // 窗口正在关闭或尚未完成显示时，忽略本次拖动。
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private async void SwitchToEditorClick(object sender, RoutedEventArgs e)
    {
        if (_projectPath is null) return;

        try
        {
            if (_viewModel.IsWorkflowActive)
                await _viewModel.StopForCloseAsync();

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("无法确定编辑器程序路径。");

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(EditorLaunchOptions.EditorSwitch);
            startInfo.ArgumentList.Add(_projectPath);
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("编辑器启动失败。");

            _keepProjectFile = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "切换编辑器失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
        if (!_keepProjectFile && _projectPath is not null)
        {
            try { File.Delete(_projectPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
