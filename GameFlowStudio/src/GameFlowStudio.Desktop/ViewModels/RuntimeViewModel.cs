using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.ViewModels;

public sealed class RuntimeScriptItem(AutomationScript script) : ObservableObject
{
    public AutomationScript Script { get; } = script;
    public string Id => Script.Id;
    public string Name => Script.Name;
    public string Description => $"{Script.Nodes.Count} 个节点";
}

/// <summary>纯运行悬浮窗的轻量状态模型，不依赖 WebView2 编辑器。</summary>
public sealed class RuntimeViewModel : ObservableObject, IDisposable
{
    private readonly IWorkflowRunner _runner;
    private readonly IWorkflowValidator _validator;
    private readonly IProjectStorageService _storage;
    private readonly ILogger<RuntimeViewModel> _logger;
    private AutomationProject _project = new();
    private RuntimeScriptItem? _selectedScript;
    private string _projectName = "未命名项目";
    private string _stateText = "未运行";
    private string _currentNodeText = "—";
    private string _progressText = "0 / 0";
    private string _errorText = string.Empty;
    private double _progressValue;
    private bool _isEnabled;
    private bool _initialized;
    private int _completedNodes;
    private int _totalNodes = 1;
    private Task? _runTask;
    private int _disposed;

    public RuntimeViewModel(
        IWorkflowRunner runner,
        IWorkflowValidator validator,
        IProjectStorageService storage,
        ILogger<RuntimeViewModel> logger)
    {
        _runner = runner;
        _validator = validator;
        _storage = storage;
        _logger = logger;
        ToggleRuntimeCommand = new RelayCommand(_ => ToggleRuntime());
        PauseResumeCommand = new RelayCommand(_ => _ = PauseOrResumeAsync(), _ => CanPauseResume);
        StopCommand = new RelayCommand(_ => _ = StopAsync(), _ => IsWorkflowActive);
        _runner.NodeStarted += OnNodeStarted;
        _runner.NodeCompleted += OnNodeCompleted;
        _runner.StateChanged += OnStateChanged;
    }

    public ObservableCollection<RuntimeScriptItem> Scripts { get; } = [];
    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }
    public RuntimeScriptItem? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (!Set(ref _selectedScript, value) || value is null || IsWorkflowActive) return;
            _project.SetActiveScript(value.Script);
            ResetProgress();
        }
    }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string CurrentNodeText { get => _currentNodeText; private set => Set(ref _currentNodeText, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public bool IsEnabled { get => _isEnabled; private set { if (Set(ref _isEnabled, value)) Raise(nameof(RunSwitchText)); } }
    public bool IsWorkflowActive => _runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;
    public bool IsSelectionEnabled => _initialized && !IsWorkflowActive;
    public bool CanPauseResume => _runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused;
    public string RunSwitchText => IsWorkflowActive ? "运行中（点击停止）" : "开启运行";
    public string PauseResumeText => _runner.State == WorkflowExecutionState.Paused ? "继续" : "暂停";
    public ICommand ToggleRuntimeCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand StopCommand { get; }

    public async Task InitializeAsync(string projectPath, string? scriptId, CancellationToken cancellationToken = default)
    {
        var project = await _storage.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
        if (project.Scripts.Count == 0) throw new InvalidDataException("项目中没有可运行的脚本。");

        _project = project;
        ProjectName = project.Project.Name;
        Scripts.Clear();
        foreach (var script in project.Scripts)
            Scripts.Add(new(script));

        var selected = Scripts.FirstOrDefault(item => item.Id == scriptId) ?? Scripts[0];
        _initialized = true;
        Raise(nameof(IsSelectionEnabled));
        SelectedScript = selected;
        ResetProgress();
    }

    public bool IsReady => _initialized;

    private void ToggleRuntime()
    {
        if (!_initialized) return;
        if (IsWorkflowActive)
        {
            _ = StopAsync();
            return;
        }

        if (_runTask is { IsCompleted: false }) return;
        IsEnabled = true;
        _runTask = RunSelectedAsync();
    }

    private async Task RunSelectedAsync()
    {
        ErrorText = string.Empty;
        ResetProgress();
        var validation = _validator.Validate(_project);
        if (!validation.IsValid)
        {
            ErrorText = validation.Errors[0].Message;
            IsEnabled = false;
            StateText = "验证失败";
            return;
        }

        try
        {
            await _runner.RunAsync(_project, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runner.State == WorkflowExecutionState.Idle)
        {
            // 停止运行是正常流程。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "纯运行脚本失败");
            OnUi(() => ErrorText = exception.Message);
        }
        finally
        {
            OnUi(() =>
            {
                IsEnabled = false;
                RaiseRuntimeCommands();
            });
        }
    }

    private async Task PauseOrResumeAsync()
    {
        if (_runner.State == WorkflowExecutionState.Paused) await _runner.ResumeAsync();
        else if (_runner.State == WorkflowExecutionState.Running) await _runner.PauseAsync();
    }

    private async Task StopAsync()
    {
        IsEnabled = false;
        await _runner.StopAsync();
    }

    public async Task StopForCloseAsync()
    {
        if (IsWorkflowActive) await StopAsync();
    }

    private void OnNodeStarted(object? sender, NodeExecutionEventArgs args) => OnUi(() => CurrentNodeText = args.Node.Data.DisplayName);

    private void OnNodeCompleted(object? sender, NodeExecutionEventArgs args) => OnUi(() =>
    {
        _completedNodes++;
        ProgressValue = Math.Clamp(_completedNodes * 100d / _totalNodes, 0, 100);
        ProgressText = $"{Math.Min(_completedNodes, _totalNodes)} / {_totalNodes}";
    });

    private void OnStateChanged(object? sender, WorkflowStateChangedEventArgs args) => OnUi(() =>
    {
        StateText = StateLabel(args.Current);
        if (args.Current is WorkflowExecutionState.Completed or WorkflowExecutionState.Failed or WorkflowExecutionState.Idle)
            IsEnabled = false;
        Raise(nameof(IsWorkflowActive));
        Raise(nameof(IsSelectionEnabled));
        Raise(nameof(CanPauseResume));
        Raise(nameof(PauseResumeText));
        Raise(nameof(RunSwitchText));
        RaiseRuntimeCommands();
    });

    private void ResetProgress()
    {
        _completedNodes = 0;
        _totalNodes = Math.Max(1, _project.Scripts.Sum(script => script.Nodes.Count));
        ProgressValue = 0;
        ProgressText = $"0 / {_totalNodes}";
        CurrentNodeText = "—";
        ErrorText = string.Empty;
    }

    private void RaiseRuntimeCommands()
    {
        (PauseResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
        _runner.NodeStarted -= OnNodeStarted;
        _runner.NodeCompleted -= OnNodeCompleted;
        _runner.StateChanged -= OnStateChanged;
        GC.SuppressFinalize(this);
    }
}
