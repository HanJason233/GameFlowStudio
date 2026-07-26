using System.IO;
using System.Windows;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Views;
using Microsoft.Win32;

namespace GameFlowStudio.Desktop.Services;

public interface IFileDialogService
{
    string? OpenProject(string? initialDirectory = null);
    string? SaveProject(string? currentPath = null);
    bool ConfirmDiscardChanges();
    void ShowError(string message);
    bool EditSettings(AppSettings settings);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenProject(string? initialDirectory = null) { var dialog = new OpenFileDialog { Filter = "GameFlow 项目 (*.gflow)|*.gflow|JSON 文件 (*.json)|*.json", InitialDirectory = initialDirectory }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public string? SaveProject(string? currentPath = null) { var dialog = new SaveFileDialog { Filter = "GameFlow 项目 (*.gflow)|*.gflow", DefaultExt = ".gflow", AddExtension = true, FileName = currentPath is null ? null : Path.GetFileName(currentPath), InitialDirectory = currentPath is null ? null : Path.GetDirectoryName(currentPath) }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public bool ConfirmDiscardChanges() => MessageBox.Show("当前项目有未保存的更改，是否放弃？", "GameFlow Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public void ShowError(string message) => MessageBox.Show(message, "GameFlow Studio", MessageBoxButton.OK, MessageBoxImage.Error);
    public bool EditSettings(AppSettings settings)
    {
        var copy = new AppSettings { WindowWidth = settings.WindowWidth, WindowHeight = settings.WindowHeight, ToolboxPaneWidth = settings.ToolboxPaneWidth, PropertiesPaneWidth = settings.PropertiesPaneWidth, OutputPaneHeight = settings.OutputPaneHeight, IsProjectFilesPaneVisible = settings.IsProjectFilesPaneVisible, IsScriptListPaneVisible = settings.IsScriptListPaneVisible, IsToolboxPaneVisible = settings.IsToolboxPaneVisible, IsPropertiesPaneVisible = settings.IsPropertiesPaneVisible, IsOutputPaneVisible = settings.IsOutputPaneVisible, StepCategoryLayout = settings.StepCategoryLayout, WindowLeft = settings.WindowLeft, WindowTop = settings.WindowTop, RecentProjects = [.. settings.RecentProjects], DefaultProjectDirectory = settings.DefaultProjectDirectory, RunHotkey = settings.RunHotkey, PauseHotkey = settings.PauseHotkey, StopHotkey = settings.StopHotkey, Theme = settings.Theme };
        if (new SettingsWindow(copy) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return false;
        settings.IsProjectFilesPaneVisible = copy.IsProjectFilesPaneVisible; settings.IsScriptListPaneVisible = copy.IsScriptListPaneVisible; settings.IsToolboxPaneVisible = copy.IsToolboxPaneVisible; settings.IsPropertiesPaneVisible = copy.IsPropertiesPaneVisible; settings.IsOutputPaneVisible = copy.IsOutputPaneVisible;
        settings.DefaultProjectDirectory = copy.DefaultProjectDirectory; settings.RunHotkey = copy.RunHotkey; settings.PauseHotkey = copy.PauseHotkey; settings.StopHotkey = copy.StopHotkey; settings.Theme = copy.Theme; settings.StepCategoryLayout = copy.StepCategoryLayout;
        return true;
    }
}

public interface ICoordinatePickerService { Task<(int X, int Y)?> PickAsync(); }
public sealed class CoordinatePickerService(Automation.Coordinates.CursorPositionService cursor) : ICoordinatePickerService
{
    public Task<(int X, int Y)?> PickAsync()
    {
        var main = Application.Current.MainWindow; var previous = main.WindowState;
        main.WindowState = WindowState.Minimized;
        var picker = new CoordinatePickerWindow(cursor); picker.ShowDialog();
        main.WindowState = previous; main.Activate();
        return Task.FromResult(picker.SelectedPosition);
    }
}
