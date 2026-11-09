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

/// <summary>Serializes project/run state transitions while keeping I/O and workflow execution outside monitor locks.</summary>
public sealed class RuntimeCommandDispatcher(
    string authenticationToken,
    IWorkflowRunner runner,
    IWorkflowValidator validator,
    IProjectStorageService storage,
    RuntimeEventPublisher events,
    RuntimeLogStore logs,
    ILogger<RuntimeCommandDispatcher> logger)
{
    private const int MaximumRememberedRuns = 256;
    private const int MaximumRunIdLength = 128;
    private readonly object _sync = new();
    private readonly Dictionary<string, RunRecord> _knownRuns = new(StringComparer.Ordinal);
    private readonly Queue<string> _runOrder = new();
    private AutomationProject? _project;
    private RunRecord? _activeRun;
    private bool _loadingProject;
    private int _initialized;
    private int _shutdownRequested;
    private int _parentProcessId;

    public bool ShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;
    public bool IsInitialized => Volatile.Read(ref _initialized) != 0;
    public int? ParentProcessId
    {
        get
        {
            var processId = Volatile.Read(ref _parentProcessId);
            return processId > 0 ? processId : null;
        }
    }
    public bool IsRunActive
    {
        get
        {
            lock (_sync)
                return _activeRun is not null || runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;
        }
    }

    public Task StopOrphanedRunAsync() => StopAsync();

    public Task<LoadProjectResponse> LoadStandaloneProjectAsync(string path, CancellationToken cancellationToken = default) =>
        LoadProjectAsync(new LoadProjectRequest(path), cancellationToken);

    public RunAcceptedResponse StartStandaloneRun(string? scriptId = null) =>
        StartRun(new StartRunRequest(Guid.NewGuid().ToString("N"), scriptId));

    public ValidateResponse ValidateStandaloneProject(string? scriptId = null) =>
        ValidateProject(new ValidateRequest(scriptId));

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

    public Task PublishWorkerReadyAsync(CancellationToken cancellationToken = default) =>
        events.PublishAsync(RuntimeEvents.WorkerReady, new RuntimeEventPayload(), cancellationToken);

    public async Task<object?> DispatchAsync(ProtocolMessage message, CancellationToken cancellationToken, bool authenticatedForConnection)
    {
        if (message.Kind != MessageKind.Command) throw new InvalidDataException("Runtime accepts command messages only.");
        if (!string.Equals(message.ProtocolVersion, ProtocolConstants.CurrentVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported protocol version: {message.ProtocolVersion}.");

        if (message.Type == RuntimeCommands.Initialize)
            return Initialize(ReadPayload<InitializeRequest>(message));

        if (!authenticatedForConnection)
            throw new UnauthorizedAccessException("This pipe connection must authenticate before sending commands.");

        return message.Type switch
        {
            RuntimeCommands.Ping => GetPing(),
            RuntimeCommands.LoadProject => await LoadProjectAsync(ReadPayload<LoadProjectRequest>(message), cancellationToken).ConfigureAwait(false),
            RuntimeCommands.Validate => ValidateProject(ReadPayload<ValidateRequest>(message)),
            RuntimeCommands.StartRun => StartRun(ReadPayload<StartRunRequest>(message)),
            RuntimeCommands.PauseRun => await PauseAsync().ConfigureAwait(false),
            RuntimeCommands.ResumeRun => await ResumeAsync().ConfigureAwait(false),
            RuntimeCommands.StopRun => await StopAsync().ConfigureAwait(false),
            RuntimeCommands.GetStatus => GetStatus(),
            RuntimeCommands.GetLogs => GetLogs(message),
            RuntimeCommands.GetRunSnapshot => GetRunSnapshot(),
            RuntimeCommands.GetEvents => GetEvents(message),
            RuntimeCommands.Shutdown => await ShutdownAsync().ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unsupported Runtime command: {message.Type}.")
        };
    }

    public async Task<LoadProjectResponse> LoadProjectAsync(LoadProjectRequest request, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_loadingProject) throw new InvalidOperationException("A project load is already in progress.");
            if (_activeRun is not null || IsRunnerActive())
                throw new InvalidOperationException("A project cannot be replaced while a workflow is active.");
            _loadingProject = true;
        }

        try
        {
            var path = Path.GetFullPath(request.SnapshotPath);
            var project = await storage.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (project.Scripts.Count == 0) throw new InvalidDataException("The project has no runnable scripts.");
            var activeScript = project.Scripts.FirstOrDefault(script => string.Equals(script.Id, request.ScriptId, StringComparison.Ordinal))
                ?? project.Scripts.FirstOrDefault(script => string.Equals(script.Id, project.ActiveScriptId, StringComparison.Ordinal))
                ?? project.Scripts[0];
            project.SetActiveScript(activeScript);

            lock (_sync)
            {
                _project = project;
                events.SetProject(project);
            }
            return ToLoadProjectResponse(project);
        }
        finally
        {
            lock (_sync) _loadingProject = false;
        }
    }

    private InitializeResponse Initialize(InitializeRequest request)
    {
        if (!FixedTimeEquals(authenticationToken, request.Token))
            throw new UnauthorizedAccessException("Runtime handshake token is invalid.");
        if (!string.Equals(request.ProtocolVersion, ProtocolConstants.CurrentVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Protocol version mismatch. Desktop requested {request.ProtocolVersion}; Runtime supports {ProtocolConstants.CurrentVersion}.");
        if (request.DesktopProcessId <= 0)
            throw new InvalidDataException("Desktop process ID must be positive.");

        Volatile.Write(ref _parentProcessId, request.DesktopProcessId);
        Volatile.Write(ref _initialized, 1);
        return new InitializeResponse(ProtocolConstants.CurrentVersion, Environment.ProcessId, true);
    }

    private ValidateResponse ValidateProject(ValidateRequest request)
    {
        AutomationProject validationProject;
        lock (_sync)
        {
            if (_loadingProject) throw new InvalidOperationException("A project load is in progress.");
            var project = _project ?? throw new InvalidOperationException("No project has been loaded.");
            var script = string.IsNullOrWhiteSpace(request.ScriptId)
                ? project.Scripts.FirstOrDefault(item => string.Equals(item.Id, project.ActiveScriptId, StringComparison.Ordinal)) ?? project.Scripts[0]
                : project.Scripts.FirstOrDefault(item => string.Equals(item.Id, request.ScriptId, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"Script {request.ScriptId} does not exist in the loaded project.");
            validationProject = new AutomationProject { Project = project.Project, Scripts = project.Scripts };
            validationProject.SetActiveScript(script);
        }

        var validation = validator.Validate(validationProject);
        return new ValidateResponse(validation.IsValid,
            validation.Errors.Select(error => new RuntimeValidationError(error.Code, error.Message, error.NodeId, error.EdgeId)).ToArray());
    }

    private RunAcceptedResponse StartRun(StartRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId)) throw new InvalidDataException("runId is required.");
        if (request.RunId.Length > MaximumRunIdLength) throw new InvalidDataException("runId exceeds the supported length.");

        lock (_sync)
        {
            if (_knownRuns.ContainsKey(request.RunId)) return new(request.RunId, true);
            if (_loadingProject) throw new InvalidOperationException("A project load is in progress.");
            if (_activeRun is not null || IsRunnerActive())
                throw new InvalidOperationException("Another workflow run is already active.");

            var project = _project ?? throw new InvalidOperationException("No project has been loaded.");
            var script = string.IsNullOrWhiteSpace(request.ScriptId)
                ? project.Scripts.FirstOrDefault(item => string.Equals(item.Id, project.ActiveScriptId, StringComparison.Ordinal)) ?? project.Scripts[0]
                : project.Scripts.FirstOrDefault(item => string.Equals(item.Id, request.ScriptId, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"Script {request.ScriptId} does not exist in the loaded project.");
            project.SetActiveScript(script);

            var record = new RunRecord(request.RunId);
            _knownRuns.Add(request.RunId, record);
            _runOrder.Enqueue(request.RunId);
            TrimRunHistoryLocked();
            _activeRun = record;
            events.BeginRun(request.RunId, project, script.Id);
            record.ExecutionTask = Task.Run(() => ExecuteRunAsync(record, project));
            return new(request.RunId, true);
        }
    }

    private async Task ExecuteRunAsync(RunRecord record, AutomationProject project)
    {
        try
        {
            var execution = runner.RunAsync(project, CancellationToken.None);
            record.Started.TrySetResult();
            await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runner.State == WorkflowExecutionState.Idle)
        {
            // StopAsync transitions the runner to Idle after its active execution unwinds.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Runtime workflow {RunId} failed", record.RunId);
            await events.PublishRunFailedAsync(record.RunId, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            record.Started.TrySetResult();
            lock (_sync)
            {
                if (ReferenceEquals(_activeRun, record)) _activeRun = null;
            }
        }
    }

    private async Task<object> PauseAsync()
    {
        var active = GetActiveRun();
        if (active is not null)
        {
            await active.Started.Task.ConfigureAwait(false);
            await runner.PauseAsync().ConfigureAwait(false);
        }
        return GetStatus();
    }

    private async Task<object> ResumeAsync()
    {
        var active = GetActiveRun();
        if (active is not null)
        {
            await active.Started.Task.ConfigureAwait(false);
            await runner.ResumeAsync().ConfigureAwait(false);
        }
        return GetStatus();
    }

    private async Task<object> StopAsync()
    {
        var active = GetActiveRun();
        if (active is not null)
        {
            await active.Started.Task.ConfigureAwait(false);
            await runner.StopAsync().ConfigureAwait(false);
            try { await active.ExecutionTask.ConfigureAwait(false); }
            catch (Exception exception) { logger.LogDebug(exception, "Runtime run completed while stopping"); }
        }
        return GetStatus();
    }

    private RuntimeStatusResponse GetStatus()
    {
        var snapshot = events.GetSnapshot([]);
        return new RuntimeStatusResponse(snapshot.State, snapshot.RunId, snapshot.CurrentNodeId, snapshot.CurrentNodeName, IsInitialized);
    }

    private RuntimePingResponse GetPing()
    {
        var snapshot = events.GetSnapshot([]);
        return new RuntimePingResponse(Environment.ProcessId, snapshot.State, snapshot.RunId, snapshot.Sequence, events.LastProgressAt);
    }

    private RuntimeRunSnapshot GetRunSnapshot() => events.GetSnapshot(logs.GetEntries());

    private RuntimeEventsResponse GetEvents(ProtocolMessage message)
    {
        var request = ReadPayload<GetEventsRequest>(message);
        if (request.AfterSequence < 0) throw new InvalidDataException("afterSequence must be nonnegative.");
        return events.GetEvents(request.AfterSequence);
    }

    private IReadOnlyList<RuntimeLogInfo> GetLogs(ProtocolMessage message)
    {
        var after = message.Payload.TryGetProperty("afterSequence", out var value) && value.TryGetInt64(out var sequence)
            ? sequence : 0;
        return logs.GetEntries(after);
    }

    private async Task<object> ShutdownAsync()
    {
        if (IsRunActive) await StopAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _shutdownRequested, 1);
        return new { shuttingDown = true };
    }

    private RunRecord? GetActiveRun()
    {
        lock (_sync) return _activeRun;
    }

    private bool IsRunnerActive() =>
        runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;

    private void TrimRunHistoryLocked()
    {
        var examined = 0;
        while (_knownRuns.Count > MaximumRememberedRuns && examined < _runOrder.Count)
        {
            var oldest = _runOrder.Dequeue();
            examined++;
            if (_activeRun?.RunId == oldest)
            {
                _runOrder.Enqueue(oldest);
                continue;
            }
            _knownRuns.Remove(oldest);
        }
    }

    private static LoadProjectResponse ToLoadProjectResponse(AutomationProject project) =>
        new(project.Project.Id, project.Project.Name, project.ActiveScriptId,
            project.Scripts.Select(script => new RuntimeScriptInfo(script.Id, script.Name, script.Nodes.Count)).ToArray());

    private void OnLogEntryAdded(object? sender, RuntimeLogInfo entry) => _ = events.PublishLogAsync(entry);

    private static T ReadPayload<T>(ProtocolMessage message) =>
        message.Payload.Deserialize<T>(ProtocolJson.Options)
        ?? throw new InvalidDataException($"Command {message.Type} has an empty payload.");

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private sealed class RunRecord(string runId)
    {
        public string RunId { get; } = runId;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecutionTask { get; set; } = Task.CompletedTask;
    }
}
