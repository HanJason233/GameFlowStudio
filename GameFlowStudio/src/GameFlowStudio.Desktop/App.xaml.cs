using System.Net.Http;
using System.IO;
using System.Windows;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Desktop.ViewModels;
using GameFlowStudio.Desktop.Views;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Infrastructure.DependencyInjection;
using GameFlowStudio.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private string? _temporaryEditorProjectPath;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logProvider = new GameFlowLogProvider();
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => { logging.ClearProviders(); logging.SetMinimumLevel(LogLevel.Debug); logging.AddProvider(logProvider); })
            .ConfigureServices((context, services) =>
            {
                services.AddGameFlowCore();
                services.AddSingleton<IImageTemplateProvider, WpfImageTemplateProvider>();
                var geminiOptions = GeminiOcrOptions.FromConfiguration(context.Configuration);
                services.AddSingleton(geminiOptions);
                services.AddSingleton(new HttpClient { Timeout = geminiOptions.Timeout });
                services.AddSingleton<IOcrService, GeminiOcrService>();
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
                services.AddSingleton<IRuntimeProcessLauncher, RuntimeProcessLauncher>();
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
                Shutdown(1);
                return;
            }
        }

        EditorLaunchOptions? editorOptions = null;
        if (EditorLaunchOptions.TryParse(e.Args, out var parsedEditorOptions))
        {
            editorOptions = parsedEditorOptions;
            _temporaryEditorProjectPath = parsedEditorOptions?.ProjectPath;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.StartupProjectPath = editorOptions?.ProjectPath;
        MainWindow = window; window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        if (_temporaryEditorProjectPath is not null)
        {
            try { File.Delete(_temporaryEditorProjectPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        base.OnExit(e);
    }
}
