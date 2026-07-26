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
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logProvider = new GameFlowLogProvider();
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => { logging.ClearProviders(); logging.SetMinimumLevel(LogLevel.Debug); logging.AddProvider(logProvider); })
            .ConfigureServices(services =>
            {
                services.AddGameFlowCore();
                services.AddSingleton<IImageTemplateProvider, WpfImageTemplateProvider>();
                services.AddSingleton(logProvider);
                services.AddSingleton<IEditorBridge, EditorBridge>();
                services.AddSingleton<IFileDialogService, FileDialogService>();
                services.AddSingleton<ICoordinatePickerService, CoordinatePickerService>();
                services.AddSingleton<IWindowSelectionService, WindowSelectionService>();
                services.AddSingleton<IRelativeCoordinatePickerService, RelativeCoordinatePickerService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            }).Build();
        await _host.StartAsync();
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window; window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        base.OnExit(e);
    }
}
