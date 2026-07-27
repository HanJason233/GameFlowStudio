using System.Windows;
using GameFlowStudio.Desktop.Views;

namespace GameFlowStudio.Desktop.Services;

public enum CoordinatePickMode
{
    Screen,
    Window
}

public interface ICoordinatePickModeService
{
    CoordinatePickMode? Select();
}

public sealed class CoordinatePickModeService : ICoordinatePickModeService
{
    public CoordinatePickMode? Select()
    {
        var dialog = new CoordinatePickModeWindow
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.SelectedMode : null;
    }
}
