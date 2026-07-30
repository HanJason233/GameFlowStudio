using System.IO;
using System.Windows;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Views;
using Microsoft.Win32;

namespace GameFlowStudio.Desktop.Services;

public interface IFileDialogService
{
    string? OpenProject(string? initialDirectory = null);
    string? OpenProgram(string? initialDirectory = null);
    string? SaveProject(string? currentPath = null);
    bool ConfirmDiscardChanges();
    bool ConfirmDiscardRecording();
    void ShowError(string message);
    bool EditSettings(AppSettings settings);
    void ShowHelp();
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenProject(string? initialDirectory = null) { var dialog = new OpenFileDialog { Filter = "GameFlow 项目 (*.gflow)|*.gflow|JSON 文件 (*.json)|*.json", InitialDirectory = initialDirectory }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public string? OpenProgram(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "程序文件 (*.exe;*.com;*.bat;*.cmd;*.lnk)|*.exe;*.com;*.bat;*.cmd;*.lnk|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? SaveProject(string? currentPath = null) { var dialog = new SaveFileDialog { Filter = "GameFlow 项目 (*.gflow)|*.gflow", DefaultExt = ".gflow", AddExtension = true, FileName = currentPath is null ? null : Path.GetFileName(currentPath), InitialDirectory = currentPath is null ? null : Path.GetDirectoryName(currentPath) }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public bool ConfirmDiscardChanges() => MessageBox.Show("当前项目有未保存的更改，是否放弃？", "GameFlow Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public bool ConfirmDiscardRecording() => MessageBox.Show("操作录制仍在进行，关闭软件会丢弃本次录制。是否继续关闭？", "GameFlow Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public void ShowError(string message) => MessageBox.Show(message, "GameFlow Studio", MessageBoxButton.OK, MessageBoxImage.Error);
    public bool EditSettings(AppSettings settings)
    {
        var copy = new AppSettings { WindowWidth = settings.WindowWidth, WindowHeight = settings.WindowHeight, ToolboxPaneWidth = settings.ToolboxPaneWidth, PropertiesPaneWidth = settings.PropertiesPaneWidth, OutputPaneHeight = settings.OutputPaneHeight, IsProjectFilesPaneVisible = settings.IsProjectFilesPaneVisible, IsScriptListPaneVisible = settings.IsScriptListPaneVisible, IsToolboxPaneVisible = settings.IsToolboxPaneVisible, IsPropertiesPaneVisible = settings.IsPropertiesPaneVisible, IsOutputPaneVisible = settings.IsOutputPaneVisible, StepCategoryLayout = settings.StepCategoryLayout, WindowLeft = settings.WindowLeft, WindowTop = settings.WindowTop, DefaultProjectDirectory = settings.DefaultProjectDirectory, GeminiApiKey = settings.GeminiApiKey, Theme = settings.Theme };
        if (new SettingsWindow(copy) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return false;
        settings.IsProjectFilesPaneVisible = copy.IsProjectFilesPaneVisible; settings.IsScriptListPaneVisible = copy.IsScriptListPaneVisible; settings.IsToolboxPaneVisible = copy.IsToolboxPaneVisible; settings.IsPropertiesPaneVisible = copy.IsPropertiesPaneVisible; settings.IsOutputPaneVisible = copy.IsOutputPaneVisible;
        settings.DefaultProjectDirectory = copy.DefaultProjectDirectory; settings.GeminiApiKey = copy.GeminiApiKey; settings.Theme = copy.Theme; settings.StepCategoryLayout = copy.StepCategoryLayout;
        return true;
    }

    public void ShowHelp() =>
        new HelpWindow { Owner = Application.Current.MainWindow }.ShowDialog();
}
