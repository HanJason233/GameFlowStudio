using System.IO;
using System.Windows;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Views;
using Microsoft.Win32;

namespace GameFlowStudio.Desktop.Services;

public interface IFileDialogService
{
    string? OpenProject(string? initialDirectory = null);
    string? SaveProject(string? currentPath = null);
    string? SaveImageTemplate(string? initialDirectory = null);
    bool ConfirmDiscardChanges();
    void ShowError(string message);
    bool EditSettings(AppSettings settings);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenProject(string? initialDirectory = null) { var dialog = new OpenFileDialog { Filter = "GameFlow 项目 (*.gflow)|*.gflow|JSON 文件 (*.json)|*.json", InitialDirectory = initialDirectory }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public string? SaveProject(string? currentPath = null) { var dialog = new SaveFileDialog { Filter = "GameFlow 项目 (*.gflow)|*.gflow", DefaultExt = ".gflow", AddExtension = true, FileName = currentPath is null ? null : Path.GetFileName(currentPath), InitialDirectory = currentPath is null ? null : Path.GetDirectoryName(currentPath) }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    public string? SaveImageTemplate(string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = "image-template.png",
            InitialDirectory = initialDirectory
        };
        if (dialog.ShowDialog() != true) return null;
        return Path.GetExtension(dialog.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : Path.ChangeExtension(dialog.FileName, ".png");
    }
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

public sealed record CoordinatePickResult(int X, int Y, string? Color = null);

public interface ICoordinatePickerService { Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null); }
public sealed class CoordinatePickerService(
    Automation.Coordinates.CursorPositionService cursor,
    IDesktopCaptureService capture) : ICoordinatePickerService
{
    public Task<CoordinatePickResult?> PickAsync(WindowBounds? relativeTo = null)
    {
        var main = Application.Current.MainWindow; var previous = main.WindowState;
        main.WindowState = WindowState.Minimized;
        var picker = new CoordinatePickerWindow(cursor, relativeTo); picker.ShowDialog();
        CoordinatePickResult? result = null;
        try
        {
            if (picker.SelectedPosition is { } position)
            {
                string? color = null;
                try
                {
                    var frame = capture.CaptureAsync(new CaptureRegion(position.X, position.Y, 1, 1), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var pixel = frame.Pixel(0, 0);
                    if (pixel.Length >= 3) color = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";
                }
                catch
                {
                    // 坐标拾取仍然有效，颜色读取失败时保留空值。
                }
                result = new CoordinatePickResult(position.X, position.Y, color);
            }
        }
        finally
        {
            main.WindowState = previous;
            main.Activate();
        }
        return Task.FromResult(result);
    }
}
