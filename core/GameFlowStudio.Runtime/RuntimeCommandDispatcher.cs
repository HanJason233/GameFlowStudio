using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Runtime;

public sealed class RuntimeCommandDispatcher(
    string authenticationToken,
    IWorkflowRunner runner,
    IWorkflowValidator validator,
    IProjectStorageService storage,
    RuntimeEventPublisher events,
    RuntimeLogStore logs,
    ILogger<RuntimeCommandDispatcher> logger)
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, byte> _knownRuns = new(StringComparer.Ordinal);
    private AutomationProject? _project;
    private string? _snapshotPath;
    private string? _activeRunId;
    private Task? _runTask;
    private bool _initialized;

    public bool ShutdownRequested { get; private set; }
    public bool IsInitialized => _initialized;
    public int? ParentProcessId { get; private set; }
    public bool IsRunActive => runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;
    public Task StopOrphanedRunAsync() => runner.StopAsync();

    public Task<LoadProjectResponse> LoadStandaloneProjectAsync(string path, CancellationToken cancellationToken = default) =>
        LoadProjectAsync(new LoadProjectRequest(path), cancellationToken);

    public RunAcceptedResponse StartStandaloneRun(string? scriptId = null) =>
        StartRun(new StartRunRequest(Guid.NewGuid().ToString("N"), scriptId));

    public void Attach(PipeMessageChannel channel)
    {
        events.Attach(channel);
        logs.EntryAdded += OnLogEntryAdded;
    }

    public void Detach(PipeMessageChannel channel)
    {
        logs.EntryAdded -= OnLogEntryAdded;
        events.Detach(channel);
    }

    public async Task<object?> DispatchAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        if (message.Kind != MessageKind.Command) throw new InvalidDataException("Runtime accepts command messages only.");
        if (!string.Equals(message.ProtocolVersion, ProtocolConstants.CurrentVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported protocol version: {message.ProtocolVersion}.");

        if (message.Type == RuntimeCommands.Initialize)
        {
            var request = ReadPayload<InitializeRequest>(message);
            if (!FixedTimeEquals(authenticationToken, request.Token)) throw new UnauthorizedAccessException("Runtime handshake token is invalid.");
            if (!string.Equals(request.ProtocolVersion, ProtocolConstants.CurrentVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"Protocol version mismatch. Desktop requested {request.ProtocolVersion}; Runtime supports {ProtocolConstants.CurrentVersion}.");
            _initialized = true;
            ParentProcessId = request.DesktopProcessId > 0 ? request.DesktopProcessId : null;
            await events.PublishAsync(RuntimeEvents.WorkerReady, new RuntimeEventPayload(), cancellationToken).ConfigureAwait(false);
            return new InitializeResponse(ProtocolConstants.CurrentVersion, Environment.ProcessId, true);
        }

        if (!_initialized) throw new UnauthorizedAccessException("Runtime must be initialized before commands are accepted.");

        return message.Type switch
        {
            RuntimeCommands.Ping => new { ready = true, processId = Environment.ProcessId },
            RuntimeCommands.LoadProject => await LoadProjectAsync(ReadPayload<LoadProjectRequest>(message), cancellationToken).ConfigureAwait(false),
            RuntimeCommands.Validate => ValidateProject(ReadPayload<ValidateRequest>(message)),
            RuntimeCommands.StartRun => StartRun(ReadPayload<StartRunRequest>(message)),
            RuntimeCommands.PauseRun => await PauseAsync().ConfigureAwait(false),
            RuntimeCommands.ResumeRun => await ResumeAsync().ConfigureAwait(false),
            RuntimeCommands.StopRun => await StopAsync().ConfigureAwait(false),
            RuntimeCommands.GetStatus => GetStatus(),
            RuntimeCommands.GetLogs => GetLogs(message),
            RuntimeCommands.Shutdown => await ShutdownAsync().ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unsupported Runtime command: {message.Type}.")
        };
    }

    private async Task<LoadProjectResponse> LoadProjectAsync(LoadProjectRequest request, CancellationToken cancellationToken)
    {
        lock (_sync)
            if (runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping)
                throw new InvalidOperationException("运行期间不能替换已加载项目。");

        var path = Path.GetFullPath(request.SnapshotPath);
        var project = await storage.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (project.Scripts.Count > 0)
        {
            var activeScript = project.Scripts.FirstOrDefault(script => string.Equals(script.Id, request.ScriptId, StringComparison.Ordinal))
                ?? project.Scripts[0];
            project.SetActiveScript(activeScript);
        }
        if (project.Scripts.Count == 0) throw new InvalidDataException("项目中没有可运行的脚本。");
        lock (_sync)
        {
            _project = project;
            _snapshotPath = path;
            _activeRunId = null;
            _runTask = null;
        }
        return new LoadProjectResponse(
            project.Project.Id,
            project.Project.Name,
            project.ActiveScriptId,
            project.Scripts.Select(script => new RuntimeScriptInfo(script.Id, script.Name, script.Nodes.Count)).ToArray());
    }

    private ValidateResponse ValidateProject(ValidateRequest request)
    {
        var project = GetProject();
        if (!string.IsNullOrWhiteSpace(request.ScriptId))
        {
            var script = project.Scripts.FirstOrDefault(item => string.Equals(item.Id, request.ScriptId, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"Script {request.ScriptId} does not exist in the loaded project.");
            project.SetActiveScript(script);
        }
        var validation = validator.Validate(project);
        return new ValidateResponse(validation.IsValid,
            validation.Errors.Select(error => new RuntimeValidationError(error.Code, error.Message, error.NodeId, error.EdgeId)).ToArray());
    }

    private RunAcceptedResponse StartRun(StartRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId)) throw new InvalidDataException("runId is required.");
        if (_knownRuns.ContainsKey(request.RunId)) return new(request.RunId, true);
        var project = GetProject();
        if (!string.IsNullOrWhiteSpace(request.ScriptId))
        {
            var script = project.Scripts.FirstOrDefault(item => string.Equals(item.Id, request.ScriptId, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"Script {request.ScriptId} does not exist in the loaded project.");
            project.SetActiveScript(script);
        }

        lock (_sync)
        {
            if (runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping)
                throw new InvalidOperationException("已有工作流正在运行。");
            if (!_knownRuns.TryAdd(request.RunId, 0)) return new(request.RunId, true);
            _activeRunId = request.RunId;
            events.BeginRun(request.RunId, project);
            _runTask = Task.Run(() => RunAsync(request.RunId, project));
        }
        return new(request.RunId, true);
    }

    private async Task RunAsync(string runId, AutomationProject project)
    {
        try
        {
            await runner.RunAsync(project, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runner.State == WorkflowExecutionState.Idle)
        {
            // Stopping a workflow is a successful lifecycle transition.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Runtime workflow {RunId} failed", runId);
            await events.PublishRunFailedAsync(runId, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task<object> PauseAsync()
    {
        await runner.PauseAsync().ConfigureAwait(false);
        return GetStatus();
    }

    private async Task<object> ResumeAsync()
    {
        await runner.ResumeAsync().ConfigureAwait(false);
        return GetStatus();
    }

    private async Task<object> StopAsync()
    {
        await runner.StopAsync().ConfigureAwait(false);
        return GetStatus();
    }

    private RuntimeStatusResponse GetStatus() => new(
        runner.State.ToString(),
        _activeRunId,
        events.CurrentNodeId,
        events.CurrentNodeName,
        _project is not null);

    private IReadOnlyList<RuntimeLogInfo> GetLogs(ProtocolMessage message)
    {
        var after = message.Payload.TryGetProperty("afterSequence", out var value) && value.TryGetInt64(out var sequence)
            ? sequence : 0;
        return logs.GetEntries(after);
    }

    private async Task<object> ShutdownAsync()
    {
        if (runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping)
            await runner.StopAsync().ConfigureAwait(false);
        ShutdownRequested = true;
        return new { shuttingDown = true };
    }

    private AutomationProject GetProject() => _project ?? throw new InvalidOperationException("No project has been loaded.");

    private void OnLogEntryAdded(object? sender, RuntimeLogInfo entry) =>
        _ = events.PublishLogAsync(entry);

    private static T ReadPayload<T>(ProtocolMessage message) =>
        message.Payload.Deserialize<T>(ProtocolJson.Options)
        ?? throw new InvalidDataException($"Command {message.Type} has an empty payload.");

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
