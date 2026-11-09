using System.Windows;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Interaction;
using GameFlowStudio.Desktop.Views;

namespace GameFlowStudio.Desktop.Services;

public interface IWindowSelectionService
{
    WindowInfo? SelectWindow();
}

public sealed class WindowSelectionService(IWindowPickerService windows) : IWindowSelectionService
{
    public WindowInfo? SelectWindow()
    {
        var dialog = new WindowSelectionWindow(windows.GetOpenWindows)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.SelectedWindow : null;
    }
}
