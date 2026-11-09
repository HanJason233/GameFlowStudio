using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Protocol;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.ViewModels;

public sealed class RuntimeScriptItem(RuntimeScriptInfo script) : ObservableObject
{
    public string Id { get; } = script.Id;
    public string Name { get; } = script.Name;
    public int NodeCount { get; } = script.NodeCount;
    public string Description { get; } = $"{script.NodeCount} 个节点";
}

/// <summary>Pure-run window UI. All loading, validation and execution stay in the separate Runtime worker.</summary>
public sealed class RuntimeViewModel : ObservableObject, IDisposable
{
    private readonly RuntimeProcessManager _runtime;
    private readonly ILogger<RuntimeViewModel> _logger;
    private RuntimeScriptItem? _selectedScript;
    private string? _projectPath;
    private string _stateText = "未运行";
    private string _currentNodeText = "—";
    private string _currentWindowTitle = "未绑定窗口";
    private string _currentWindowHandleText = "—";
    private string _progressText = "0 / 0";
    private string _errorText = string.Empty;
    private double _progressValue;
    private bool _initialized;
    private int _completedNodes;
    private int _totalNodes = 1;
    private string? _runId;
    private long _lastEventSequence;
    private Task? _runTask;
    private int _disposed;
    private WorkflowExecutionState _state;

    public RuntimeViewModel(RuntimeProcessManager runtime, ILogger<RuntimeViewModel> logger)
    {
        _runtime = runtime;
        _logger = logger;
        ToggleRuntimeCommand = new RelayCommand(_ => ToggleRuntime());
        StopCommand = new RelayCommand(_ => _ = StopAsync(), _ => IsWorkflowActive);
        _runtime.RuntimeEventReceived += OnRuntimeEvent;
        _runtime.StatusRecovered += OnStatusRecovered;
        _runtime.RunSnapshotRecovered += OnRunSnapshotRecovered;
        _runtime.ConnectionLost += OnConnectionLost;
    }

    public ObservableCollection<RuntimeScriptItem> Scripts { get; } = [];
    public RuntimeScriptItem? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (!Set(ref _selectedScript, value) || value is null || IsWorkflowActive) return;
            _totalNodes = Math.Max(1, value.NodeCount);
            ResetProgress();
        }
    }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string CurrentNodeText { get => _currentNodeText; private set => Set(ref _currentNodeText, value); }
    public string CurrentWindowTitle { get => _currentWindowTitle; private set => Set(ref _currentWindowTitle, value); }
    public string CurrentWindowHandleText { get => _currentWindowHandleText; private set => Set(ref _currentWindowHandleText, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public bool IsWorkflowActive => _state is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;
    public bool IsSelectionEnabled => _initialized && !IsWorkflowActive;
    public string RunSwitchText => _state switch
    {
        WorkflowExecutionState.Running => "暂停",
        WorkflowExecutionState.Paused => "继续运行",
        WorkflowExecutionState.Stopping => "正在停止",
        _ => "开始运行"
    };
    public ICommand ToggleRuntimeCommand { get; }
    public ICommand StopCommand { get; }

    public async Task InitializeAsync(string projectPath, string? scriptId, CancellationToken cancellationToken = default)
    {
        _projectPath = Path.GetFullPath(projectPath);
        var project = await _runtime.LoadProjectAsync(_projectPath, scriptId, cancellationToken).ConfigureAwait(false);
        if (project.Scripts.Count == 0) throw new InvalidDataException("项目中没有可运行的脚本。");

        OnUi(() =>
        {
            Scripts.Clear();
            foreach (var script in project.Scripts) Scripts.Add(new(script));
            _initialized = true;
            Raise(nameof(IsSelectionEnabled));
            SelectedScript = Scripts.FirstOrDefault(item => item.Id == scriptId)
                ?? Scripts.FirstOrDefault(item => item.Id == project.ActiveScriptId)
                ?? Scripts[0];
            _totalNodes = Math.Max(1, SelectedScript.NodeCount);
            ResetProgress();
        });
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        OnStatusRecovered(status);
    }

    public bool IsReady => _initialized;

    private void ToggleRuntime()
    {
        if (!_initialized) return;
        if (_state is WorkflowExecutionState.Running or WorkflowExecutionState.Paused)
        {
            _ = PauseOrResumeAsync();
            return;
        }
        if (_state == WorkflowExecutionState.Stopping || _runTask is { IsCompleted: false }) return;
        _runTask = RunSelectedAsync();
    }

    private async Task RunSelectedAsync()
    {
        if (SelectedScript is null || string.IsNullOrWhiteSpace(_projectPath)) return;
        ErrorText = string.Empty;
        ResetProgress();
        try
        {
            if (!File.Exists(_projectPath)) throw new FileNotFoundException("纯运行项目快照已不存在。", _projectPath);
            var validation = await _runtime.ValidateAsync(SelectedScript.Id).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                OnUi(() =>
                {
                    ErrorText = validation.Errors.FirstOrDefault()?.Message ?? "工作流验证失败。";
                    SetState(WorkflowExecutionState.Failed);
                });
                return;
            }

            _runId = Guid.NewGuid().ToString("N");
            var accepted = await _runtime.StartRunAsync(_runId, SelectedScript.Id).ConfigureAwait(false);
            if (!accepted.Accepted) throw new InvalidOperationException("Runtime 拒绝开始工作流。");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "纯运行脚本失败");
            OnUi(() => ErrorText = exception.Message);
        }
    }

    private async Task PauseOrResumeAsync()
    {
        try
        {
            if (_state == WorkflowExecutionState.Paused) await _runtime.ResumeRunAsync().ConfigureAwait(false);
            else if (_state == WorkflowExecutionState.Running) await _runtime.PauseRunAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { OnUi(() => ErrorText = exception.Message); }
    }

    private async Task StopAsync()
    {
        try { await _runtime.StopRunAsync().ConfigureAwait(false); }
        catch (Exception exception) { OnUi(() => ErrorText = exception.Message); }
    }

    public async Task StopForCloseAsync()
    {
        if (!IsWorkflowActive) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _runtime.StopRunAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Runtime did not stop before the pure-run window closed");
            OnUi(() => ErrorText = exception.Message);
        }
    }

    private void OnRuntimeEvent(ProtocolMessage message)
    {
        if (message.Kind != MessageKind.Event) return;
        RuntimeEventPayload? payload;
        try { payload = message.Payload.Deserialize<RuntimeEventPayload>(ProtocolJson.Options); }
        catch (Exception exception) { _logger.LogWarning(exception, "无法解析 Runtime 事件 {EventType}", message.Type); return; }
        if (payload is null) return;
        OnUi(() => ApplyRuntimeEvent(message.Type, payload, message.Sequence));
    }

    private void ApplyRuntimeEvent(string eventType, RuntimeEventPayload payload, long envelopeSequence)
    {
        var sequence = envelopeSequence > 0 ? envelopeSequence : payload.Sequence;
        if (sequence > 0)
        {
            if (sequence <= _lastEventSequence) return;
            _lastEventSequence = sequence;
        }
        if (_runId is not null && payload.RunId is not null && payload.RunId != _runId) return;
        switch (eventType)
        {
            case RuntimeEvents.StateChanged:
                if (Enum.TryParse<WorkflowExecutionState>(payload.State, out var state)) SetState(state);
                break;
            case RuntimeEvents.NodeStarted:
                CurrentNodeText = payload.NodeName ?? "—";
                UpdateWindow(payload);
                break;
            case RuntimeEvents.NodeCompleted:
                UpdateWindow(payload);
                break;
            case RuntimeEvents.ProgressChanged when payload.CompletedNodes is { } completed:
                _completedNodes = Math.Max(0, completed);
                _totalNodes = Math.Max(1, payload.TotalNodes ?? _totalNodes);
                ProgressValue = Math.Clamp(payload.ProgressPercent ?? (_completedNodes * 100d / _totalNodes), 0, 100);
                ProgressText = $"{_completedNodes} / {_totalNodes}";
                break;
            case RuntimeEvents.NodeFailed:
                UpdateWindow(payload);
                ErrorText = payload.Error ?? "节点执行失败。";
                break;
            case RuntimeEvents.RunCompleted:
                ProgressValue = 100;
                ProgressText = $"{_completedNodes} / {_totalNodes}";
                break;
            case RuntimeEvents.RunFailed:
                ErrorText = payload.Error ?? "工作流执行失败。";
                break;
        }
    }

    private void OnStatusRecovered(RuntimeStatusResponse status) => OnUi(() =>
    {
        var wasRunning = _state is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;
        if (wasRunning && _runId is not null && status.RunId != _runId
            && status.State is nameof(WorkflowExecutionState.Idle) or nameof(WorkflowExecutionState.Completed) or nameof(WorkflowExecutionState.Failed))
        {
            ErrorText = "Runtime 连接中断后，本次运行已结束。";
            _runId = null;
        }
        if (Enum.TryParse<WorkflowExecutionState>(status.State, out var state)) SetState(state);
        if (status.CurrentNodeId is { } nodeId) CurrentNodeText = status.CurrentNodeName ?? nodeId;
    });

    private void OnRunSnapshotRecovered(RuntimeRunSnapshot snapshot) => OnUi(() =>
    {
        var wasRunning = IsWorkflowActive;
        var interrupted = wasRunning && _runId is not null && snapshot.RunId != _runId
            && snapshot.State is nameof(WorkflowExecutionState.Idle) or nameof(WorkflowExecutionState.Completed) or nameof(WorkflowExecutionState.Failed);
        _lastEventSequence = snapshot.Sequence;
        _completedNodes = snapshot.CompletedNodes;
        _totalNodes = Math.Max(1, snapshot.TotalNodes);
        ProgressValue = Math.Clamp(snapshot.ProgressPercent, 0, 100);
        ProgressText = $"{_completedNodes} / {_totalNodes}";
        CurrentNodeText = snapshot.CurrentNodeName ?? snapshot.CurrentNodeId ?? "—";
        if (interrupted) ErrorText = "Runtime 已重启，本次运行已中断；为避免重复桌面操作，不会自动重放。";
        _runId = snapshot.RunId;
        if (Enum.TryParse<WorkflowExecutionState>(snapshot.State, out var state)) SetState(state);
        if (_runId is not null && state == WorkflowExecutionState.Failed)
            ErrorText = snapshot.Nodes.FirstOrDefault(node => node.State == "Failed")?.Error ?? "工作流运行失败。";
    });

    private void OnConnectionLost(Exception? exception)
    {
        if (exception is not null) _logger.LogWarning(exception, "Runtime IPC 连接已断开，正在尝试重连");
    }

    private void SetState(WorkflowExecutionState state)
    {
        _state = state;
        StateText = StateLabel(state);
        if (state == WorkflowExecutionState.Completed)
        {
            ProgressValue = 100;
            ProgressText = $"{_totalNodes} / {_totalNodes}";
        }
        Raise(nameof(IsWorkflowActive));
        Raise(nameof(IsSelectionEnabled));
        Raise(nameof(RunSwitchText));
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ResetProgress()
    {
        _completedNodes = 0;
        ProgressValue = 0;
        ProgressText = $"0 / {_totalNodes}";
        CurrentNodeText = "—";
        CurrentWindowTitle = "未绑定窗口";
        CurrentWindowHandleText = "—";
        ErrorText = string.Empty;
    }

    private void UpdateWindow(RuntimeEventPayload args)
    {
        CurrentWindowTitle = string.IsNullOrWhiteSpace(args.WindowTitle) ? "未绑定窗口" : args.WindowTitle;
        CurrentWindowHandleText = args.WindowHandle is { } handle ? $"0x{handle:X}" : "—";
    }

    private void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private static string StateLabel(WorkflowExecutionState state) => state switch
    {
        WorkflowExecutionState.Running => "正在运行",
        WorkflowExecutionState.Paused => "已暂停",
        WorkflowExecutionState.Stopping => "正在停止",
        WorkflowExecutionState.Completed => "已完成",
        WorkflowExecutionState.Failed => "执行失败",
        _ => "未运行"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _runtime.RuntimeEventReceived -= OnRuntimeEvent;
        _runtime.StatusRecovered -= OnStatusRecovered;
        _runtime.RunSnapshotRecovered -= OnRunSnapshotRecovered;
        _runtime.ConnectionLost -= OnConnectionLost;
        GC.SuppressFinalize(this);
    }
}
