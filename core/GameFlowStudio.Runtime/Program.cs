using System.Security.Cryptography;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Infrastructure.DependencyInjection;
using GameFlowStudio.Infrastructure.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Runtime;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = RuntimeLaunchArguments.Parse(args);
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        var logStore = new RuntimeLogStore();
        builder.Logging.AddProvider(logStore);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddSingleton(logStore);
        builder.Services.AddGameFlowCore();
        builder.Services.AddSingleton<IImageTemplateProvider, OpenCvImageTemplateProvider>();
        var ocrOptions = GeminiOcrOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(ocrOptions);
        builder.Services.AddSingleton(new HttpClient { Timeout = ocrOptions.Timeout });
        builder.Services.AddSingleton<IOcrService, GeminiOcrService>();
        builder.Services.AddSingleton<RuntimeEventPublisher>();

        using var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);
        var services = host.Services;
        var events = services.GetRequiredService<RuntimeEventPublisher>();
        events.ObserveRunner(services.GetRequiredService<IWorkflowRunner>());
        var dispatcher = new RuntimeCommandDispatcher(
            options.Token,
            services.GetRequiredService<IWorkflowRunner>(),
            services.GetRequiredService<IWorkflowValidator>(),
            services.GetRequiredService<IProjectStorageService>(),
            events,
            logStore,
            services.GetRequiredService<ILogger<RuntimeCommandDispatcher>>());
        if (!string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            _ = await dispatcher.LoadStandaloneProjectAsync(options.ProjectPath).ConfigureAwait(false);
            if (options.StartImmediately) _ = dispatcher.StartStandaloneRun(options.ScriptId);
        }
        using var shutdown = new CancellationTokenSource();
        var server = new RuntimePipeServer(
            options.PipeName,
            dispatcher,
            () => shutdown.Cancel(),
            options.IsManaged,
            services.GetRequiredService<ILogger<RuntimePipeServer>>());

        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            await host.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            services.GetRequiredService<ILogger<RuntimePipeServer>>().LogCritical(exception, "Runtime host stopped unexpectedly");
            return 1;
        }
    }
}

internal sealed record RuntimeLaunchArguments(string PipeName, string Token, bool IsManaged, string? ProjectPath, string? ScriptId, bool StartImmediately)
{
    public static RuntimeLaunchArguments Parse(IReadOnlyList<string> args)
    {
        string? pipe = null;
        string? token = null;
        string? project = null;
        string? scriptId = null;
        var startImmediately = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--pipe", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                pipe = args[++index];
            else if (string.Equals(args[index], "--token", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                token = args[++index];
            else if (string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                project = Path.GetFullPath(args[++index]);
            else if (string.Equals(args[index], "--script", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                scriptId = args[++index];
            else if (string.Equals(args[index], "--run", StringComparison.OrdinalIgnoreCase))
                startImmediately = true;
        }
        pipe ??= $"GameFlowStudio.Runtime.{Environment.ProcessId}.{Guid.NewGuid():N}";
        token ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (string.IsNullOrWhiteSpace(pipe)) throw new ArgumentException("Pipe name is required.");
        if (startImmediately && string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("--run requires --project <path>.");
        return new RuntimeLaunchArguments(pipe, token,
            args.Any(argument => string.Equals(argument, "--pipe", StringComparison.OrdinalIgnoreCase)),
            project, scriptId, startImmediately);
    }
}
