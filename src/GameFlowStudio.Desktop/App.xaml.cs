using System.IO;
using System.Windows;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Desktop.ViewModels;
using GameFlowStudio.Desktop.Views;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Desktop.Interaction;
using GameFlowStudio.Desktop.Logging;
using GameFlowStudio.Storage.Projects;
using GameFlowStudio.Storage.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private readonly object _hostShutdownSync = new();
    private Task? _hostShutdownTask;
    private string? _temporaryEditorProjectPath;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logProvider = new GameFlowLogProvider();
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => { logging.ClearProviders(); logging.SetMinimumLevel(LogLevel.Debug); logging.AddProvider(logProvider); })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IProjectStorageService, ProjectStorageService>();
                services.AddSingleton<ISettingsStorageService, SettingsStorageService>();
                services.AddSingleton<IDesktopCaptureService, DesktopCaptureService>();
                services.AddSingleton<IWindowPickerService, DesktopWindowPickerService>();
                services.AddSingleton<IInputRecordingService, GlobalInputRecordingService>();
                services.AddSingleton<CursorPositionService>();
                services.AddSingleton<IImageCaptureService, ImageCaptureService>();
                services.AddSingleton<IColorPickerService, ColorPickerService>();
                services.AddSingleton(logProvider);
                services.AddSingleton<IEditorBridge, EditorBridge>();
                services.AddSingleton<IFileDialogService, FileDialogService>();
                services.AddSingleton<ICoordinatePickerService, CoordinatePickerService>();
                services.AddSingleton<IRectanglePickerService, RectanglePickerService>();
                services.AddSingleton<ICoordinatePickModeService, CoordinatePickModeService>();
                services.AddSingleton<IWindowSelectionService, WindowSelectionService>();
                services.AddSingleton<IRelativeCoordinatePickerService, RelativeCoordinatePickerService>();
                services.AddSingleton<RuntimeClient>();
                services.AddSingleton<RuntimeProcessManager>();
                services.AddSingleton<IPureRunWindowLauncher, PureRunWindowLauncher>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddTransient<RuntimeViewModel>();
                services.AddTransient<RuntimeWindow>();
            }).Build();
        await _host.StartAsync();

        if (RuntimeLaunchOptions.TryParse(e.Args, out var runtimeOptions) && runtimeOptions is not null)
        {
            try
            {
                var runtimeWindow = _host.Services.GetRequiredService<RuntimeWindow>();
                await runtimeWindow.InitializeAsync(runtimeOptions);
                MainWindow = runtimeWindow;
                runtimeWindow.Show();
                return;
            }
            catch (Exception exception)
            {
                try { File.Delete(runtimeOptions.ProjectPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                MessageBox.Show(exception.Message, "纯运行启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                await ShutdownHostAsync();
                Shutdown(1);
                return;
            }
        }

        EditorLaunchOptions? editorOptions = null;
        if (EditorLaunchOptions.TryParse(e.Args, out var parsedEditorOptions))
        {
            editorOptions = parsedEditorOptions;
            _temporaryEditorProjectPath = parsedEditorOptions is { DeleteOnExit: true } ? parsedEditorOptions.ProjectPath : null;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.StartupProjectPath = editorOptions?.ProjectPath;
        MainWindow = window; window.Show();
    }

    public Task ShutdownHostAsync()
    {
        lock (_hostShutdownSync)
            return _hostShutdownTask ??= ShutdownHostCoreAsync();
    }

    private async Task ShutdownHostCoreAsync()
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            var logger = host.Services.GetService<ILogger<App>>();
            try { await host.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { logger?.LogError(exception, "Desktop host failed to stop cleanly"); }
            try
            {
                if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
                else host.Dispose();
            }
            catch (Exception exception) { logger?.LogError(exception, "Desktop host failed to release its resources"); }
        }
        if (_temporaryEditorProjectPath is not null)
        {
            try { File.Delete(_temporaryEditorProjectPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _temporaryEditorProjectPath = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var logger = _host.Services.GetService<ILogger<App>>();
            logger?.LogWarning("Application exit reached before asynchronous window shutdown completed; starting best-effort cleanup.");
            _ = ShutdownHostAsync();
        }
        base.OnExit(e);
    }
}
