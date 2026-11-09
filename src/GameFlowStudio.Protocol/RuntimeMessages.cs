namespace GameFlowStudio.Protocol;

public static class RuntimeCommands
{
    public const string Initialize = "initialize";
    public const string LoadProject = "load_project";
    public const string Validate = "validate";
    public const string StartRun = "start_run";
    public const string PauseRun = "pause_run";
    public const string ResumeRun = "resume_run";
    public const string StopRun = "stop_run";
    public const string GetStatus = "get_status";
    public const string GetLogs = "get_logs";
    public const string Shutdown = "shutdown";
    public const string Ping = "ping";
    public const string GetRunSnapshot = "get_run_snapshot";
    public const string GetEvents = "get_events";
}

public static class RuntimeEvents
{
    public const string WorkerReady = "worker_ready";
    public const string StateChanged = "state_changed";
    public const string NodeStarted = "node_started";
    public const string NodeCompleted = "node_completed";
    public const string NodeFailed = "node_failed";
    public const string ProgressChanged = "progress_changed";
    public const string LogReceived = "log_received";
    public const string RunCompleted = "run_completed";
    public const string RunFailed = "run_failed";
    public const string RecordingActionReceived = "recording_action_received";
}

public sealed record InitializeRequest(string Token, string ProtocolVersion, int DesktopProcessId);
public sealed record InitializeResponse(string ProtocolVersion, int RuntimeProcessId, bool Ready);
public sealed record LoadProjectRequest(string SnapshotPath, string? ScriptId = null);
public sealed record ValidateRequest(string? ScriptId = null);
public sealed record RuntimeScriptInfo(string Id, string Name, int NodeCount);
public sealed record LoadProjectResponse(string ProjectId, string ProjectName, string? ActiveScriptId, IReadOnlyList<RuntimeScriptInfo> Scripts);
public sealed record RuntimeValidationError(string Code, string Message, string? NodeId = null, string? EdgeId = null);
public sealed record ValidateResponse(bool IsValid, IReadOnlyList<RuntimeValidationError> Errors);
public sealed record StartRunRequest(string RunId, string? ScriptId);
public sealed record RunAcceptedResponse(string RunId, bool Accepted);
public sealed record RuntimeStatusResponse(string State, string? RunId, string? CurrentNodeId, string? CurrentNodeName, bool IsInitialized);
public sealed record RuntimePingResponse(int RuntimeProcessId, string State, string? RunId, long EventSequence, DateTimeOffset LastProgressAt);
public sealed record RuntimeLogInfo(long Sequence, DateTimeOffset Timestamp, string Level, string Category, string Message, string? Exception = null);
public sealed record RuntimeNodeSnapshot(string ScriptId, string NodeId, string NodeName, string State, string? Error,
    long? WindowHandle, string? WindowTitle, long Sequence);
public sealed record RuntimeRunSnapshot(string? RunId, string State, string? CurrentScriptId, string? CurrentNodeId,
    string? CurrentNodeName, int CompletedNodes, int TotalNodes, double ProgressPercent, long Sequence,
    IReadOnlyList<RuntimeNodeSnapshot> Nodes, IReadOnlyList<RuntimeLogInfo> Logs);
public sealed record RuntimeEventInfo(long Sequence, string Type, RuntimeEventPayload Payload);
public sealed record RuntimeEventsResponse(long RequestedAfterSequence, long LastSequence, bool HistoryTruncated,
    IReadOnlyList<RuntimeEventInfo> Events);
public sealed record GetEventsRequest(long AfterSequence);

/// <summary>Versioned event body. Optional fields are populated for the corresponding event type.</summary>
public sealed record RuntimeEventPayload
{
    public string? RunId { get; init; }
    public long Sequence { get; init; }
    public string? PreviousState { get; init; }
    public string? State { get; init; }
    public string? NodeId { get; init; }
    public string? ScriptId { get; init; }
    public string? NodeName { get; init; }
    public string? Error { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public long? WindowHandle { get; init; }
    public string? WindowTitle { get; init; }
    public int? CompletedNodes { get; init; }
    public int? TotalNodes { get; init; }
    public double? ProgressPercent { get; init; }
    public RuntimeLogInfo? Log { get; init; }
}
