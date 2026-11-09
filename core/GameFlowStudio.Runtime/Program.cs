using System.Security.Cryptography;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Infrastructure.DependencyInjection;
using GameFlowStudio.Infrastructure.Ocr;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Runtime;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RuntimeLaunchArguments options;
        try { options = RuntimeLaunchArguments.Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(RuntimeLaunchArguments.Usage);
            return 2;
        }

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

        var host = builder.Build();
        await using var hostDisposal = (IAsyncDisposable)host;
        try
        {
            await host.StartAsync().ConfigureAwait(false);
            var services = host.Services;
            var runner = services.GetRequiredService<IWorkflowRunner>();
            var events = services.GetRequiredService<RuntimeEventPublisher>();
            events.ObserveRunner(runner);

            if (options.Mode == RuntimeLaunchMode.Standalone)
                return await RunStandaloneAsync(options, runner, services.GetRequiredService<IProjectStorageService>(),
                    services.GetRequiredService<IWorkflowValidator>()).ConfigureAwait(false);

            var dispatcher = new RuntimeCommandDispatcher(
                options.Token!,
                runner,
                services.GetRequiredService<IWorkflowValidator>(),
                services.GetRequiredService<IProjectStorageService>(),
                events,
                logStore,
                services.GetRequiredService<ILogger<RuntimeCommandDispatcher>>());
            if (options.ProjectPath is not null)
            {
                await dispatcher.LoadStandaloneProjectAsync(options.ProjectPath).ConfigureAwait(false);
                if (options.StartImmediately)
                {
                    var validation = dispatcher.ValidateStandaloneProject(options.ScriptId);
                    if (!validation.IsValid)
                    {
                        foreach (var error in validation.Errors) Console.Error.WriteLine(error.Message);
                        return 2;
                    }
                    _ = dispatcher.StartStandaloneRun(options.ScriptId);
                }
            }

            using var shutdown = new CancellationTokenSource();
            var server = new RuntimePipeServer(
                options.PipeName!,
                dispatcher,
                () => shutdown.Cancel(),
                options.Mode == RuntimeLaunchMode.Managed,
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
        catch (Exception exception)
        {
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GameFlowStudio.Runtime.Program")
                .LogCritical(exception, "Runtime startup failed");
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunStandaloneAsync(
        RuntimeLaunchArguments options,
        IWorkflowRunner runner,
        IProjectStorageService storage,
        IWorkflowValidator validator)
    {
        var project = await storage.LoadAsync(options.ProjectPath!, CancellationToken.None).ConfigureAwait(false);
        if (project.Scripts.Count == 0)
        {
            Console.Error.WriteLine("The project has no runnable scripts.");
            return 2;
        }
        var script = project.Scripts.FirstOrDefault(item => item.Id == options.ScriptId)
            ?? project.Scripts.FirstOrDefault(item => item.Id == project.ActiveScriptId)
            ?? project.Scripts[0];
        project.SetActiveScript(script);
        Console.WriteLine($"Loaded '{project.Project.Name}' — {script.Name} ({script.Nodes.Count} nodes).");
        if (!options.StartImmediately) return 0;

        var validation = validator.Validate(project);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors) Console.Error.WriteLine(error.Message);
            return 2;
        }
        runner.NodeStarted += (_, node) => Console.WriteLine($"[{node.ScriptId}] {node.Node.Data.DisplayName}");
        runner.NodeFailed += (_, node) => Console.Error.WriteLine($"Failed: {node.Node.Data.DisplayName}: {node.Error}");
        runner.StateChanged += (_, state) => Console.WriteLine($"State: {state.Current}");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await runner.RunAsync(project, cancellation.Token).ConfigureAwait(false);
            return cancellation.IsCancellationRequested ? 130 : runner.State == WorkflowExecutionState.Completed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }
}

internal enum RuntimeLaunchMode
{
    Standalone,
    Managed,
    ExternalWorker
}

internal sealed record RuntimeLaunchArguments(
    RuntimeLaunchMode Mode,
    string? PipeName,
    string? Token,
    string? ProjectPath,
    string? ScriptId,
    bool StartImmediately)
{
    public const string Usage = "Usage: GameFlowStudio.Runtime.exe --project <file.gflow> [--script <id>] [--run] | --pipe <name> --token <token> [--managed] [--project <file.gflow>] [--script <id>] [--run]";

    public static RuntimeLaunchArguments Parse(IReadOnlyList<string> args)
    {
        string? pipe = null;
        string? token = null;
        string? project = null;
        string? scriptId = null;
        var managed = false;
        var run = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--managed", StringComparison.OrdinalIgnoreCase)) managed = true;
            else if (string.Equals(argument, "--run", StringComparison.OrdinalIgnoreCase)) run = true;
            else if (string.Equals(argument, "--pipe", StringComparison.OrdinalIgnoreCase)) pipe = NextValue(args, ref index, argument);
            else if (string.Equals(argument, "--token", StringComparison.OrdinalIgnoreCase)) token = NextValue(args, ref index, argument);
            else if (string.Equals(argument, "--project", StringComparison.OrdinalIgnoreCase)) project = Path.GetFullPath(NextValue(args, ref index, argument));
            else if (string.Equals(argument, "--script", StringComparison.OrdinalIgnoreCase)) scriptId = NextValue(args, ref index, argument);
            else throw new ArgumentException($"Unknown Runtime argument: {argument}.");
        }

        if (string.IsNullOrWhiteSpace(pipe))
        {
            if (managed || token is not null) throw new ArgumentException("--managed and --token require --pipe.");
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Standalone mode requires --project <path>.");
            return new RuntimeLaunchArguments(RuntimeLaunchMode.Standalone, null, null, project, scriptId, run);
        }

        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Pipe mode requires --token <token>.");
        if (run && string.IsNullOrWhiteSpace(project)) throw new ArgumentException("--run requires --project <path>.");
        return new RuntimeLaunchArguments(managed ? RuntimeLaunchMode.Managed : RuntimeLaunchMode.ExternalWorker,
            pipe, token, project, scriptId, run);
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{option} requires a value.");
        return args[++index];
    }
}
