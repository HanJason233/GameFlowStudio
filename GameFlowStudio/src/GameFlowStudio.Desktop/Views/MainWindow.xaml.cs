using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using GameFlowStudio.Automation.Hotkeys;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Desktop.ViewModels;

namespace GameFlowStudio.Desktop.Views;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private readonly MainViewModel _viewModel;
    private readonly IEditorBridge _editor;
    private readonly GlobalHotkeyRegistrar _hotkeys;

    public MainWindow(MainViewModel viewModel, IEditorBridge editor, GlobalHotkeyRegistrar hotkeys)
    {
        InitializeComponent(); _viewModel = viewModel; _editor = editor; _hotkeys = hotkeys; DataContext = viewModel;
        Loaded += OnLoaded; SourceInitialized += OnSourceInitialized; Closing += OnClosing; viewModel.HotkeysChanged += (_, _) => RegisterHotkeys();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            Width = Math.Max(MinWidth, _viewModel.Settings.WindowWidth); Height = Math.Max(MinHeight, _viewModel.Settings.WindowHeight);
            if (_viewModel.Settings.WindowLeft.HasValue && _viewModel.Settings.WindowTop.HasValue) { Left = _viewModel.Settings.WindowLeft.Value; Top = _viewModel.Settings.WindowTop.Value; }
            RegisterHotkeys();
            await _editor.InitializeAsync(EditorWebView);
        }
        catch (Exception ex) { _viewModel.ReportHotkeyError(ex); }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this); source.AddHook(WindowProcedure);
        _hotkeys.Initialize(source.Handle); RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        try { _hotkeys.Clear(); _hotkeys.Register(1, _viewModel.Settings.RunHotkey); _hotkeys.Register(2, _viewModel.Settings.PauseHotkey); _hotkeys.Register(3, _viewModel.Settings.StopHotkey); }
        catch (Exception ex) { _viewModel.ReportHotkeyError(ex); }
    }

    private void ScriptRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScriptListItem item })
        {
            e.Handled = true;
            _viewModel.OpenScriptPropertiesCommand.Execute(item);
        }
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey) { handled = true; _viewModel.HandleHotkey(wParam.ToInt32()); }
        return 0;
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (!_viewModel.CanClose()) e.Cancel = true; else _hotkeys.Dispose(); }
}
