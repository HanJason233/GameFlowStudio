using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Recording;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Infrastructure.Logging;
using GameFlowStudio.Workflow.Recording;
using Microsoft.Extensions.Logging;
using ScriptInteractionModeValue = GameFlowStudio.Contracts.Enums.ScriptInteractionMode;

namespace GameFlowStudio.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IEditorBridge _editor;
    private readonly IWorkflowRunner _runner;
    private readonly IWorkflowValidator _validator;
    private readonly IProjectStorageService _storage;
    private readonly ISettingsStorageService _settingsStorage;
    private readonly IFileDialogService _dialogs;
    private readonly ICoordinatePickerService _coordinatePicker;
    private readonly IColorPickerService _colorPicker;
    private readonly IRectanglePickerService _rectanglePicker;
    private readonly IImageCaptureService _imageCapture;
    private readonly ICoordinatePickModeService _coordinatePickMode;
    private readonly IWindowSelectionService _windowSelection;
    private readonly IWindowAutomationService _windows;
    private readonly IRelativeCoordinatePickerService _relativeCoordinatePicker;
    private readonly IRuntimeProcessLauncher _runtimeLauncher;
    private readonly IInputRecordingService _inputRecorder;
    private readonly ILogger<MainViewModel> _logger;
    private readonly object _settingsSaveSync = new();
    private readonly ConcurrentDictionary<WorkflowNode, byte> _failedExecutionNodes = new();
    private Task _settingsSaveChain = Task.CompletedTask;
    private AutomationProject _project = ProjectFactory.CreateNew();
    private string _activeScriptId = string.Empty;
    private string? _currentPath;
    private string _projectName = "未命名项目";
    private string _stateText = "未运行";
    private string _activeNavigationItem = "scripts";
    private string _currentNodeText = "—";
    private string _elapsedText = "—";
    private bool _dirty;
    private bool _runtimeLaunchInProgress;
    private bool _isProjectOperationInProgress;
    private bool _deferCommandStateNotifications;
    private bool _isWorkspaceInputBlocked;
    private bool _isRecordingStarting;
    private bool _isRecordingFinalizing;
    private bool _editorReadOnlyRequested;
    private string? _copiedProjectJson;
    private string? _copiedProjectActiveScriptId;
    private string? _copiedScriptJson;
    private string? _copiedScriptSourceProjectId;
    private string? _copiedScriptSourceId;
    private int _recordedActionCount;
    private int _recordingStatusNotificationPending;
    private InputRecordingScope _recordingScope = InputRecordingScope.Screen;
    private WindowInfo? _recordingWindow;
    private string? _recordingScriptId;
    private string? _recordingTargetEndNodeId;
    private SelectedNodeViewModel? _selectedNode;
    private bool _suppressSelectedNodeUpdates;
    private readonly Stopwatch _executionTimer = new();
    private GridLength _toolboxPaneWidth = new(220);
    private GridLength _propertiesPaneWidth = new(320);
    private GridLength _outputPaneHeight = new(220);
    private GridLength _lastToolboxPaneWidth = new(220);
    private GridLength _lastPropertiesPaneWidth = new(320);
    private GridLength _lastOutputPaneHeight = new(220);
    private GridLength _projectFilesPaneWidth = new(210);
    private GridLength _scriptListPaneWidth = new(220);
    private GridLength _lastProjectFilesPaneWidth = new(210);
    private GridLength _lastScriptListPaneWidth = new(220);
    private bool _isProjectFilesPaneVisible = true;
    private bool _isScriptListPaneVisible = true;
    private bool _isToolboxPaneVisible = true;
    private bool _isPropertiesPaneVisible = true;
    private bool _isOutputPaneVisible = true;
    private bool _isHorizontalStepLayout;
    private bool _isScriptPropertiesVisible;
    private bool _scriptWindowScopeEnabled;
    private string _scriptWindowMatchPattern = ".*";
    private string _scriptWindowExcludePattern = string.Empty;
    private WindowExecutionMode _scriptOperationFlow = WindowExecutionMode.Serial;
    private ScriptInteractionModeValue _scriptInteractionMode = ScriptInteractionModeValue.Foreground;
    private string _activeScriptName = string.Empty;
    private int _scriptDefaultNodeDelayMs = AutomationScript.DefaultNodeDelayMilliseconds;

    public MainViewModel(IEditorBridge editor, IWorkflowRunner runner, IWorkflowValidator validator, IProjectStorageService storage,
        ISettingsStorageService settingsStorage, IFileDialogService dialogs, ICoordinatePickerService coordinatePicker,
        IColorPickerService colorPicker,
        IRectanglePickerService rectanglePicker, IImageCaptureService imageCapture,
        ICoordinatePickModeService coordinatePickMode,
        IWindowSelectionService windowSelection, IWindowAutomationService windows, IRelativeCoordinatePickerService relativeCoordinatePicker,
        IRuntimeProcessLauncher runtimeLauncher, IInputRecordingService inputRecorder,
        GameFlowLogProvider logProvider, ILogger<MainViewModel> logger)
    {
        _editor = editor; _runner = runner; _validator = validator; _storage = storage; _settingsStorage = settingsStorage;
        _dialogs = dialogs; _coordinatePicker = coordinatePicker; _colorPicker = colorPicker; _rectanglePicker = rectanglePicker; _imageCapture = imageCapture;
        _coordinatePickMode = coordinatePickMode; _windowSelection = windowSelection; _windows = windows; _relativeCoordinatePicker = relativeCoordinatePicker; _runtimeLauncher = runtimeLauncher; _inputRecorder = inputRecorder; _logger = logger;
        ToolboxGroups = NodeCatalog.Groups;
        EnsureScriptModel(_project);
        InitializeCommands();
        SubscribeToEvents(logProvider);
        RestoreLogEntries(logProvider);
    }

    public ObservableCollection<DisplayLogEntry> Logs { get; } = [];
    public ObservableCollection<ScriptListItem> Scripts { get; } = [];
    private static readonly IReadOnlyList<OperationFlowModeOption> ForegroundOperationFlowModes =
    [
        new(WindowExecutionMode.PseudoParallel, "伪并行", "每个窗口依次切换进行操作，实现操作进度一致。"),
        new(WindowExecutionMode.Serial, "串行", "每个窗口完整执行完操作，再进行下一个窗口。")
    ];
    private static readonly IReadOnlyList<OperationFlowModeOption> BackgroundOperationFlowModes =
    [
        new(WindowExecutionMode.Parallel, "并行", "每个窗口同时进行操作。")
    ];
    public IReadOnlyList<OperationFlowModeOption> OperationFlowModes =>
        ScriptInteractionMode == ScriptInteractionModeValue.Background
            ? BackgroundOperationFlowModes
            : ForegroundOperationFlowModes;
    public IReadOnlyList<ScriptInteractionModeOption> ScriptInteractionModes { get; } =
    [
        new(ScriptInteractionModeValue.Foreground, "前台"),
        new(ScriptInteractionModeValue.Background, "后台")
    ];
    public IReadOnlyList<ToolboxGroup> ToolboxGroups { get; }
    public AppSettings Settings { get; private set; } = new();
    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }
    public string ActiveScriptId => ActiveScript.Id;
    public string ActiveScriptName
    {
        get => _activeScriptName;
        set
        {
            var normalized = NormalizeScriptName(value);
            if (!Set(ref _activeScriptName, normalized)) return;
            ActiveScript.Name = normalized;
            var listItem = Scripts.FirstOrDefault(script => script.Id == ActiveScript.Id);
            if (listItem is not null) listItem.Name = normalized;
            MarkScriptSettingsChanged();
        }
    }
    public string CurrentFileName => _currentPath is null ? "未保存项目.gflow" : Path.GetFileName(_currentPath);
    public string CurrentPathText => _currentPath ?? "尚未保存到磁盘";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string CurrentNodeText { get => _currentNodeText; private set => Set(ref _currentNodeText, value); }
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public bool IsRecording => _isRecordingStarting || _inputRecorder.IsRecording || _isRecordingFinalizing;
    // 录制时允许当前脚本卡片上的“停止”按钮继续响应；画布自身仍由只读状态保护。
    public bool IsEditorInteractionEnabled => IsRecording || IsProjectEditingEnabled;
    public bool IsWorkspaceInputEnabled => !_isWorkspaceInputBlocked;
    public string RecordingStatusText => _isRecordingStarting
        ? "正在准备录制…"
        : _isRecordingFinalizing
            ? "正在把录制步骤追加到当前脚本…"
            : $"正在录制（{RecordingScopeLabel()}） · {Volatile.Read(ref _recordedActionCount)} 个操作 · F7 停止";
    public SelectedNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (Set(ref _selectedNode, value))
            {
                Raise(nameof(HasSelectedNode));
                Raise(nameof(HasPropertySelection));
                Raise(nameof(HasDetectorConditionSelection));
                Raise(nameof(IsRegularNodeSelection));
                RaiseCommandStates();
            }
        }
    }
    public bool HasSelectedNode => SelectedNode is not null;
    public bool HasDetectorConditionSelection => SelectedNode?.IsDetectorCondition == true;
    public bool IsRegularNodeSelection => SelectedNode is { IsDetectorCondition: false };
    public bool HasPropertySelection => HasSelectedNode || IsScriptPropertiesVisible;
    public bool IsProjectEditingEnabled =>
        !IsWorkflowActive()
        && !IsRecording
        && !_runtimeLaunchInProgress
        && !_isProjectOperationInProgress;
    public bool IsScriptPropertiesVisible
    {
        get => _isScriptPropertiesVisible;
        private set
        {
            if (Set(ref _isScriptPropertiesVisible, value)) Raise(nameof(HasPropertySelection));
        }
    }
    public bool ScriptWindowScopeEnabled
    {
        get => _scriptWindowScopeEnabled;
        set
        {
            if (!Set(ref _scriptWindowScopeEnabled, value)) return;
            ActiveScript.WindowScope.Enabled = value;
            Raise(nameof(UseWholeScreen));
            MarkScriptSettingsChanged();
        }
    }
    public bool UseWholeScreen
    {
        get => !ScriptWindowScopeEnabled;
        set
        {
            if (value) ScriptWindowScopeEnabled = false;
            else ScriptWindowScopeEnabled = true;
        }
    }
    public string ScriptWindowMatchPattern
    {
        get => _scriptWindowMatchPattern;
        set { if (Set(ref _scriptWindowMatchPattern, value)) { ActiveScript.WindowScope.MatchPattern = value; MarkScriptSettingsChanged(); } }
    }
    public string ScriptWindowExcludePattern
    {
        get => _scriptWindowExcludePattern;
        set { if (Set(ref _scriptWindowExcludePattern, value)) { ActiveScript.WindowScope.ExcludePattern = value; MarkScriptSettingsChanged(); } }
    }
    public WindowExecutionMode ScriptOperationFlow
    {
        get => _scriptOperationFlow;
        set
        {
            var normalized = NormalizeOperationFlow(ScriptInteractionMode, value);
            if (!Set(ref _scriptOperationFlow, normalized)) return;
            ActiveScript.WindowScope.ExecutionMode = normalized;
            Raise(nameof(ScriptOperationFlowDescription));
            MarkScriptSettingsChanged();
        }
    }
    public string ScriptOperationFlowDescription =>
        OperationFlowModes.FirstOrDefault(mode => mode.Value == ScriptOperationFlow)?.Description ?? string.Empty;
    public ScriptInteractionModeValue ScriptInteractionMode
    {
        get => _scriptInteractionMode;
        set
        {
            if (!Set(ref _scriptInteractionMode, value)) return;
            ActiveScript.InteractionMode = value;
            var normalizedFlow = NormalizeOperationFlow(value, _scriptOperationFlow);
            if (_scriptOperationFlow != normalizedFlow)
            {
                _scriptOperationFlow = normalizedFlow;
                ActiveScript.WindowScope.ExecutionMode = normalizedFlow;
                Raise(nameof(ScriptOperationFlow));
            }
            Raise(nameof(OperationFlowModes));
            Raise(nameof(ScriptOperationFlowDescription));
            MarkScriptSettingsChanged();
        }
    }
    public int ScriptDefaultNodeDelayMs
    {
        get => _scriptDefaultNodeDelayMs;
        set
        {
            var normalized = Math.Clamp(value, 0, 60000);
            if (Set(ref _scriptDefaultNodeDelayMs, normalized)) { ActiveScript.DefaultNodeDelayMs = normalized; MarkScriptSettingsChanged(); }
        }
    }
    public GridLength ToolboxPaneWidth { get => _toolboxPaneWidth; set { if (Set(ref _toolboxPaneWidth, value)) Raise(nameof(LeftSidebarWidth)); } }
    public GridLength PropertiesPaneWidth { get => _propertiesPaneWidth; set => Set(ref _propertiesPaneWidth, value); }
    public GridLength OutputPaneHeight { get => _outputPaneHeight; set => Set(ref _outputPaneHeight, value); }
    public GridLength ProjectFilesPaneWidth { get => _projectFilesPaneWidth; set { if (Set(ref _projectFilesPaneWidth, value)) Raise(nameof(LeftSidebarWidth)); } }
    public GridLength ScriptListPaneWidth { get => _scriptListPaneWidth; set { if (Set(ref _scriptListPaneWidth, value)) Raise(nameof(LeftSidebarWidth)); } }
    public GridLength LeftSidebarWidth
    {
        get => ActiveNavigationItem switch
        {
            "project" => ProjectFilesPaneWidth,
            "scripts" => ScriptListPaneWidth,
            "toolbox" => ToolboxPaneWidth,
            _ => new GridLength(0)
        };
        set
        {
            switch (ActiveNavigationItem)
            {
                case "project": ProjectFilesPaneWidth = value; break;
                case "scripts": ScriptListPaneWidth = value; break;
                case "toolbox": ToolboxPaneWidth = value; break;
            }
        }
    }
    public bool IsProjectFilesPaneVisible
    {
        get => _isProjectFilesPaneVisible;
        private set
        {
            if (!Set(ref _isProjectFilesPaneVisible, value)) return;
            RaiseLeftSidebarLayout();
        }
    }
    public bool IsScriptListPaneVisible
    {
        get => _isScriptListPaneVisible;
        private set
        {
            if (!Set(ref _isScriptListPaneVisible, value)) return;
            RaiseLeftSidebarLayout();
        }
    }
    public bool IsToolboxPaneVisible { get => _isToolboxPaneVisible; private set { if (Set(ref _isToolboxPaneVisible, value)) RaiseLeftSidebarLayout(); } }
    public bool IsPropertiesPaneVisible { get => _isPropertiesPaneVisible; private set { if (Set(ref _isPropertiesPaneVisible, value)) { Raise(nameof(PropertiesPaneMinWidth)); Raise(nameof(IsPropertiesNavigationActive)); } } }
    public bool IsOutputPaneVisible { get => _isOutputPaneVisible; private set { if (Set(ref _isOutputPaneVisible, value)) { Raise(nameof(OutputPaneMinHeight)); Raise(nameof(IsOutputNavigationActive)); } } }
    public string ActiveNavigationItem
    {
        get => _activeNavigationItem;
        private set
        {
            if (!Set(ref _activeNavigationItem, value)) return;
            Raise(nameof(IsProjectFilesNavigationActive));
            Raise(nameof(IsScriptListNavigationActive));
            Raise(nameof(IsToolboxNavigationActive));
            RaiseLeftSidebarLayout();
        }
    }
    public bool IsProjectFilesNavigationActive => ActiveNavigationItem == "project";
    public bool IsScriptListNavigationActive => ActiveNavigationItem == "scripts";
    public bool IsToolboxNavigationActive => ActiveNavigationItem == "toolbox";
    public bool IsPropertiesNavigationActive => IsPropertiesPaneVisible;
    public bool IsOutputNavigationActive => IsOutputPaneVisible;
    public bool IsHorizontalStepLayout { get => _isHorizontalStepLayout; private set => Set(ref _isHorizontalStepLayout, value); }
    public double PropertiesPaneMinWidth => IsPropertiesPaneVisible ? 220 : 0;
    public double OutputPaneMinHeight => IsOutputPaneVisible ? 120 : 0;
    public double LeftSidebarMinWidth => IsLeftSidebarVisible ? 190 : 0;
    public bool IsLeftSidebarVisible => IsProjectFilesPaneVisible || IsScriptListPaneVisible || IsToolboxPaneVisible;
    public ICommand NewCommand { get; private set; } = null!; public ICommand OpenCommand { get; private set; } = null!; public ICommand SaveCommand { get; private set; } = null!;
    public ICommand SaveAsCommand { get; private set; } = null!; public ICommand OpenSampleCommand { get; private set; } = null!; public ICommand RunCommand { get; private set; } = null!;
    public ICommand RuntimeLaunchCommand { get; private set; } = null!;
    public ICommand StartRecordingCommand { get; private set; } = null!;
    public ICommand StopRecordingCommand { get; private set; } = null!;
    public ICommand PauseCommand { get; private set; } = null!; public ICommand ResumeCommand { get; private set; } = null!; public ICommand StopCommand { get; private set; } = null!;
    public ICommand AddNodeCommand { get; private set; } = null!; public ICommand PickCoordinateCommand { get; private set; } = null!; public ICommand SelectProgramPathCommand { get; private set; } = null!;
    public ICommand NewScriptCommand { get; private set; } = null!;
    public ICommand SelectScriptCommand { get; private set; } = null!;
    public ICommand OpenScriptPropertiesCommand { get; private set; } = null!;
    public ICommand DeleteScriptCommand { get; private set; } = null!;
    public ICommand CopyProjectCommand { get; private set; } = null!;
    public ICommand PasteProjectCommand { get; private set; } = null!;
    public ICommand CopyScriptCommand { get; private set; } = null!;
    public ICommand PasteScriptCommand { get; private set; } = null!;
    public ICommand ToggleProjectFilesPaneCommand { get; private set; } = null!;
    public ICommand ToggleScriptListPaneCommand { get; private set; } = null!;
    public ICommand SettingsCommand { get; private set; } = null!;
    public ICommand HelpCommand { get; private set; } = null!;
    public ICommand ToggleToolboxPaneCommand { get; private set; } = null!;
    public ICommand TogglePropertiesPaneCommand { get; private set; } = null!;
    public ICommand ToggleOutputPaneCommand { get; private set; } = null!;
    public ICommand NavigatePaneCommand { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Settings = await _settingsStorage.LoadAsync(CancellationToken.None);
        ApplyPaneLayout();
        ApplyStepLayout();
        ProjectName = _project.Project.Name;
        if (_editor.IsReady) await _editor.SendAsync("initialize_editor", CreateEditorSnapshot(ActiveScript));
    }

    public Task LoadStartupProjectAsync(string path) =>
        LoadProjectAsync(path, rememberPath: true, markDirty: false);

    public bool CanClose()
    {
        if (!_runtimeLaunchInProgress
            && (_isRecordingStarting || _isRecordingFinalizing || _isProjectOperationInProgress))
            return false;
        if (_inputRecorder.IsRecording && !_dialogs.ConfirmDiscardRecording()) return false;
        if (!_runtimeLaunchInProgress
            && (_isRecordingStarting || _isRecordingFinalizing || _isProjectOperationInProgress))
            return false;
        if (!_runtimeLaunchInProgress && _dirty && !_dialogs.ConfirmDiscardChanges()) return false;
        if (!_runtimeLaunchInProgress
            && (_isRecordingStarting || _isRecordingFinalizing || _isProjectOperationInProgress))
            return false;
        StopActiveRecording();
        StopActiveWorkflow();
        CaptureWindowSettings();
        CapturePaneLayout();
        PersistSettings();
        WaitForPendingSettingsSaves();
        return true;
    }

    private async void OnEditorMessage(object? sender, EditorMessage message)
    {
        try
        {
            switch (message.Type)
            {
                  case "editor_ready":
                      await _editor.SendAsync("initialize_editor", CreateEditorSnapshot(ActiveScript));
                      if (_editorReadOnlyRequested)
                          await SetEditorReadOnlyAsync(true);
                      break;
                  case "workflow_changed": _dirty = true; break;
                  case "editor_error":
                      _logger.LogError("Editor reported an error: {Payload}", message.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "unknown" : message.Payload.GetRawText());
                      break;
                  case "node_selected": ReadSelection(message.Payload); break;
                  case "node_context_menu":
                      ReadSelection(message.Payload);
                      if (!IsPropertiesPaneVisible) TogglePropertiesPane();
                      break;
              }
        }
        catch (Exception ex) { _logger.LogError(ex, "处理编辑器消息失败"); }
    }

    private void ReadSelection(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Null || payload.ValueKind == JsonValueKind.Undefined) { SelectedNode = null; IsScriptPropertiesVisible = false; return; }
        IsScriptPropertiesVisible = false;
        var data = payload.GetProperty("data");
        var selected = new SelectedNodeViewModel { Id = payload.GetProperty("nodeId").GetString()!, Type = payload.GetProperty("nodeType").GetString()!, DisplayName = data.GetProperty("displayName").GetString() ?? string.Empty };
        selected.SetChangedHandler(OnSelectedNodeEditorChanged);
        if (data.TryGetProperty("parameters", out var parameters))
        {
            foreach (var property in parameters.EnumerateObject())
            {
                var displayValue = DisplayValue(property.Value);
                var value = property.Name switch
                {
                    "coordinateMode" => displayValue,
                    _ => displayValue
                };
                selected.Parameters.Add(new(property.Name, value, property.Value.ValueKind, OnSelectedNodeEditorChanged));
            }
        }
        if (selected.SupportsCoordinateMode && !selected.Parameters.Any(parameter => parameter.Name == "coordinateMode"))
            selected.Parameters.Insert(0, new("coordinateMode", "followScript", JsonValueKind.String, OnSelectedNodeEditorChanged));
        if (selected.Type == NodeTypes.ImageCondition && !selected.Parameters.Any(parameter => parameter.Name == "imageSearchMode"))
            selected.Parameters.Insert(selected.Parameters.Any(parameter => parameter.Name == "coordinateMode") ? 1 : 0, new("imageSearchMode", "fixed", JsonValueKind.String, OnSelectedNodeEditorChanged));
        if (selected.Type == NodeTypes.ImageCondition && !selected.Parameters.Any(parameter => parameter.Name == "imageMatchEngine"))
            selected.Parameters.Add(new("imageMatchEngine", "custom", JsonValueKind.String, OnSelectedNodeEditorChanged));
        if (selected.Type == NodeTypes.OcrCondition && !selected.Parameters.Any(parameter => parameter.Name == "textSearchMode"))
            selected.Parameters.Insert(selected.Parameters.Any(parameter => parameter.Name == "coordinateMode") ? 1 : 0, new("textSearchMode", "fixed", JsonValueKind.String, OnSelectedNodeEditorChanged));
        if (selected.Type == NodeTypes.OcrCondition && !selected.Parameters.Any(parameter => parameter.Name == "ocrEngine"))
            selected.Parameters.Add(new("ocrEngine", "gemini", JsonValueKind.String, OnSelectedNodeEditorChanged));
        if (selected.IsDetectorCondition)
        {
            var confidenceName = selected.Type == NodeTypes.ColorCondition ? "minMatchRatio" : "matchThreshold";
            var defaultConfidence = selected.Type == NodeTypes.ColorCondition ? "1" : selected.Type == NodeTypes.OcrCondition ? "0.8" : "0.95";
            if (!selected.Parameters.Any(parameter => parameter.Name == confidenceName))
                selected.Parameters.Add(new(confidenceName, defaultConfidence, JsonValueKind.Number, OnSelectedNodeEditorChanged));
        }
        SelectedNode = selected;
    }

    private void OnSelectedNodeEditorChanged()
    {
        SelectedNode?.RefreshParameterViews();
        if (!_suppressSelectedNodeUpdates)
            ObserveTask(UpdateSelectedNodeAsync(), "更新节点属性失败");
    }

    private async Task UpdateSelectedNodeAsync()
    {
        if (SelectedNode is null) return;
        if (!NodeParameterCodec.TryParse(SelectedNode.Parameters, out var parameters, out _)) return;

        await _editor.SendAsync("update_node_data", new { nodeId = SelectedNode.Id, data = new { displayName = SelectedNode.DisplayName, parameters } });
    }

    private async Task PickCoordinateAsync()
    {
        if (SelectedNode is null) return;
        var mode = _coordinatePickMode.Select();
        if (mode is null) return;

        WindowInfo? window = null;
        if (mode == CoordinatePickMode.Window)
        {
            window = _windowSelection.SelectWindow();
            if (window is null) return;
        }

        if (SelectedNode.Type == NodeTypes.ColorCondition)
        {
            await PickColorAsync(mode.Value, window);
            return;
        }

        if (SelectedNode.Type == NodeTypes.ImageCondition)
        {
            await PickImageAsync(mode.Value, window);
            return;
        }

        if (SelectedNode.Type == NodeTypes.OcrCondition)
        {
            await PickOcrRegionAsync(mode.Value, window);
            return;
        }

        var first = await PickCoordinateAsync(mode.Value, window);
        if (first is null) return;
        CoordinatePickResult? second = null;
        if (SelectedNode.Type == NodeTypes.MouseDrag)
        {
            second = await PickCoordinateAsync(mode.Value, window);
            if (second is null) return;
        }

        await ApplySelectedNodeChangesAsync(() =>
        {
            SetParameter("coordinateMode", mode == CoordinatePickMode.Screen ? "absolute" : "relative");
            if (second is not null)
            {
                SetParameter("startX", first.X); SetParameter("startY", first.Y);
                SetParameter("endX", second.X); SetParameter("endY", second.Y);
            }
            else
            {
                SetParameter("x", first.X); SetParameter("y", first.Y);
            }
        });
    }

    private async Task SelectProgramPathAsync()
    {
        if (SelectedNode?.CanPickProgramPath != true) return;

        var currentPath = SelectedNode.GetParameterValue("path", string.Empty);
        var initialDirectory = Path.GetDirectoryName(currentPath);
        var path = _dialogs.OpenProgram(initialDirectory);
        if (path is null) return;

        await ApplySelectedNodeChangesAsync(() => SetParameter("path", path));
    }

    private async Task PickColorAsync(CoordinatePickMode mode, WindowInfo? window)
    {
        WindowBounds? bounds = window is null ? null : _windows.GetWindowBounds(window.Handle);
        var result = await _colorPicker.PickAsync(bounds);
        if (result is null) return;

        var x = mode == CoordinatePickMode.Screen ? result.X : result.X - bounds!.Left;
        var y = mode == CoordinatePickMode.Screen ? result.Y : result.Y - bounds!.Top;
        await ApplySelectedNodeChangesAsync(() =>
        {
            SetParameter("coordinateMode", mode == CoordinatePickMode.Screen ? "absolute" : "relative");
            SetParameter("x", x);
            SetParameter("y", y);
            if (result.Color is not null) SetParameter("targetColor", result.Color);
        });
    }

    private async Task PickImageAsync(CoordinatePickMode mode, WindowInfo? window)
    {
        WindowBounds? bounds = window is null ? null : _windows.GetWindowBounds(window.Handle);
        CaptureRegion region;
        string path;

        try
        {
            if (await _rectanglePicker.PickAndCaptureAsync(bounds) is not { } selection) return;

            region = selection.Region;
            path = _imageCapture.CreatePrivateTemplatePath();
            await _imageCapture.SaveAsync(selection.Frame, path);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存图判断模板截图失败");
            _dialogs.ShowError($"保存截图失败：{exception.Message}");
            return;
        }

        var x = mode == CoordinatePickMode.Screen ? region.X : region.X - bounds!.Left;
        var y = mode == CoordinatePickMode.Screen ? region.Y : region.Y - bounds!.Top;
        await ApplySelectedNodeChangesAsync(() =>
        {
            SetParameter("coordinateMode", mode == CoordinatePickMode.Screen ? "absolute" : "relative");
            SetParameter("x", x);
            SetParameter("y", y);
            SetParameter("width", region.Width);
            SetParameter("height", region.Height);
            SetParameter("templatePath", path);
        });
    }

    private async Task PickOcrRegionAsync(CoordinatePickMode mode, WindowInfo? window)
    {
        WindowBounds? bounds = window is null ? null : _windows.GetWindowBounds(window.Handle);
        if (await _rectanglePicker.PickAsync(bounds) is not { } region) return;

        var x = mode == CoordinatePickMode.Screen ? region.X : region.X - bounds!.Left;
        var y = mode == CoordinatePickMode.Screen ? region.Y : region.Y - bounds!.Top;
        await ApplySelectedNodeChangesAsync(() =>
        {
            SetParameter("coordinateMode", mode == CoordinatePickMode.Screen ? "absolute" : "relative");
            SetParameter("x", x);
            SetParameter("y", y);
            SetParameter("width", region.Width);
            SetParameter("height", region.Height);
        });
    }

    private Task<CoordinatePickResult?> PickCoordinateAsync(CoordinatePickMode mode, WindowInfo? window)
    {
        return mode == CoordinatePickMode.Window && window is not null
            ? _relativeCoordinatePicker.PickAsync(window)
            : _coordinatePicker.PickAsync();
    }

    private async Task ApplySelectedNodeChangesAsync(Action changes)
    {
        _suppressSelectedNodeUpdates = true;
        try { changes(); }
        finally { _suppressSelectedNodeUpdates = false; }
        await UpdateSelectedNodeAsync();
    }

    private void SetParameter(string name, int value)
    {
        var item = SelectedNode!.Parameters.FirstOrDefault(p => p.Name == name);
        if (item is null) SelectedNode.Parameters.Add(new(name, value.ToString(CultureInfo.InvariantCulture), JsonValueKind.Number, OnSelectedNodeEditorChanged)); else item.Value = value.ToString(CultureInfo.InvariantCulture);
    }

    private void SetParameter(string name, string value)
    {
        var item = SelectedNode!.Parameters.FirstOrDefault(p => p.Name == name);
        if (item is null) SelectedNode.Parameters.Add(new(name, value, JsonValueKind.String, OnSelectedNodeEditorChanged)); else item.Value = value;
    }

    private async Task NewAsync()
    {
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;
        _project = ProjectFactory.CreateNew(); EnsureScriptModel(_project);
        _currentPath = null; _dirty = false; ProjectName = _project.Project.Name; Raise(nameof(CurrentFileName)); Raise(nameof(CurrentPathText)); SelectedNode = null; IsScriptPropertiesVisible = false;
        await _editor.SendAsync("import_workflow", CreateEditorSnapshot(ActiveScript));
    }

    private async Task CopyProjectAsync()
    {
        var project = await ExportAsync();
        _copiedProjectJson = JsonSerializer.Serialize(project, ProjectJson.Options);
        _copiedProjectActiveScriptId = _activeScriptId;
        RaiseCommandStates();
        _logger.LogInformation("已复制项目 {ProjectName}", project.Project.Name);
    }

    private async Task PasteProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(_copiedProjectJson)) return;
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;

        var project = JsonSerializer.Deserialize<AutomationProject>(_copiedProjectJson, ProjectJson.Options)
            ?? throw new InvalidDataException("复制的项目内容为空。");
        var now = DateTimeOffset.UtcNow;
        project.Project.Id = $"project-{Guid.NewGuid():N}";
        project.Project.Name = CreateProjectCopyName(project.Project.Name);
        project.Project.CreatedAt = now;
        project.Project.UpdatedAt = now;

        if (project.Scripts.Count == 0)
            throw new InvalidDataException("复制的项目不包含脚本。");
        foreach (var script in project.Scripts)
        {
            script.WindowScope ??= new();
            script.DefaultNodeDelayMs = Math.Clamp(script.DefaultNodeDelayMs, 0, 60000);
        }
        var activeScript = project.Scripts.FirstOrDefault(
            script => script.Id == _copiedProjectActiveScriptId) ?? project.Scripts[0];
        project.SetActiveScript(activeScript);

        // 编辑器确认导入成功后再替换当前项目，失败时原项目与画布保持一致。
        await ImportEditorWorkflowAsync(CreateEditorSnapshot(project, activeScript));
        _project = project;
        _activeScriptId = activeScript.Id;
        SyncRuntimeScriptView(activeScript);
        RefreshScripts();
        RefreshScriptSettings();
        _currentPath = null;
        _dirty = true;
        ProjectName = _project.Project.Name;
        Raise(nameof(CurrentFileName));
        Raise(nameof(CurrentPathText));
        SelectedNode = null;
        IsScriptPropertiesVisible = false;
        _logger.LogInformation("已粘贴项目副本 {ProjectName}", _project.Project.Name);
    }

    private async Task OpenAsync()
    {
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;
        var path = _dialogs.OpenProject(Settings.DefaultProjectDirectory); if (path is null) return;
        await LoadProjectAsync(path);
    }

    private async Task OpenSampleAsync()
    {
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "basic-example.gflow");
        if (!File.Exists(path)) { _dialogs.ShowError("找不到示例项目文件。"); return; }
        await LoadProjectAsync(path, false);
    }

    private async Task LoadProjectAsync(
        string path,
        bool rememberPath = true,
        bool markDirty = false)
    {
        try
        {
            var project = await _storage.LoadAsync(path, CancellationToken.None);
            ApplyLoadedProject(project, path, rememberPath, markDirty);
            await _editor.SendAsync("import_workflow", CreateEditorSnapshot(ActiveScript));
            _logger.LogInformation("已打开项目 {Path}", path);
        }
        catch (Exception ex) { ReportOperationError(ex, "打开项目失败"); }
    }

    private async Task SaveAsync(bool saveAs)
    {
        try
        {
            var project = await ExportAsync();
            var path = ResolveSavePath(saveAs);
            if (path is null) return;
            await _storage.SaveAsync(project, path, CancellationToken.None);
            ApplySavedProject(project, path);
        }
        catch (Exception ex) { ReportOperationError(ex, "保存项目失败"); }
    }

    private async Task RunAsync()
    {
        try
        {
            var project = await ExportAsync();
            _failedExecutionNodes.Clear();
            await _editor.SendAsync("clear_execution_state");
            if (!await ValidateForRunAsync(project)) return;
            await ExecuteWorkflowAsync(project);
        }
        catch (Exception ex) { ReportOperationError(ex, "运行失败"); }
        finally { await FinishRunAsync(); }
    }

    private async Task LaunchRuntimeAsync()
    {
        if (IsWorkflowActive()) return;

        try
        {
            var project = await ExportAsync();
            if (!await ValidateForRunAsync(project)) return;
            if (_dirty || _currentPath is null)
            {
                await SaveAsync(false);
                if (_dirty || _currentPath is null) return;
                project = _project;
            }
            await _runtimeLauncher.LaunchAsync(project, _currentPath, CancellationToken.None);
            _runtimeLaunchInProgress = true;
            Application.Current?.Shutdown();
        }
        catch (Exception exception)
        {
            ReportOperationError(exception, "启动纯运行窗口失败");
        }
    }

    private async Task AddNodeAsync(ToolboxItem? item)
    {
        if (item is not null)
            await _editor.SendAsync("add_node", new { nodeType = item.Type });
    }

    private async Task StartRecordingAsync()
    {
        if (IsRecording || IsWorkflowActive()) return;
        var mode = _coordinatePickMode.Select();
        if (mode is null) return;

        WindowInfo? window = null;
        if (mode == CoordinatePickMode.Window)
        {
            window = _windowSelection.SelectWindow();
            if (window is null) return;
        }

        _isRecordingStarting = true;
        Interlocked.Exchange(ref _recordedActionCount, 0);
        RaiseRecordingState();
        Exception? failure = null;
        try
        {
            await SetEditorReadOnlyAsync(true);
            await ExportAsync();
            _recordingScope = mode == CoordinatePickMode.Window
                ? InputRecordingScope.Window
                : InputRecordingScope.Screen;
            _recordingWindow = window;
            _recordingScriptId = ActiveScript.Id;
            _recordingTargetEndNodeId = ResolveRecordingTargetEndNodeId();
            _inputRecorder.Start(new InputRecordingOptions
            {
                Scope = _recordingScope,
                TargetWindowHandle = window?.Handle ?? nint.Zero
            });
            _logger.LogInformation(
                "已开始录制键鼠操作，范围：{RecordingScope}；按 F7 停止录制。",
                RecordingScopeLabel());
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            _isRecordingStarting = false;
            try
            {
                if (!_inputRecorder.IsRecording && _editorReadOnlyRequested)
                    await SetEditorReadOnlyAsync(false);
            }
            catch (Exception unlockException)
            {
                _logger.LogError(unlockException, "录制启动失败后恢复编辑器只读状态失败");
                failure ??= unlockException;
            }
            finally
            {
                if (!_inputRecorder.IsRecording) ClearRecordingContext();
                RaiseRecordingState();
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task StopRecordingAsync()
    {
        if (!_inputRecorder.IsRecording || _isRecordingFinalizing) return;
        _isRecordingFinalizing = true;
        RaiseRecordingState();
        Exception? failure = null;
        try
        {
            var actions = _inputRecorder.Stop();
            Interlocked.Exchange(ref _recordedActionCount, actions.Count);
            Raise(nameof(RecordingStatusText));
            if (actions.Count == 0)
            {
                _logger.LogInformation("录制结束，未捕获到可回放操作，当前脚本未发生变化。");
            }
            else
            {
                if (_recordingScriptId is null
                    || _recordingTargetEndNodeId is null
                    || ActiveScript.Id != _recordingScriptId)
                    throw new InvalidOperationException("录制期间当前脚本发生变化，无法安全追加录制步骤。");

                var current = ActiveScript;
                var candidate = RecordedScriptFactory.Append(
                    current,
                    actions,
                    new RecordedScriptAppendOptions
                    {
                        TargetEndNodeId = _recordingTargetEndNodeId,
                        Scope = _recordingScope,
                        WindowTitle = _recordingWindow?.Title,
                        RecordWaitTime = Settings.RecordWaitTimeDuringRecording,
                        MergeKeyboardInput = Settings.MergeContinuousKeyboardInput
                    });
                await ImportEditorWorkflowAsync(candidate);
                var scriptIndex = _project.Scripts.FindIndex(script => script.Id == current.Id);
                if (scriptIndex < 0)
                    throw new InvalidOperationException("找不到录制开始时的当前脚本。");
                _project.Scripts[scriptIndex] = candidate;
                _dirty = true;
                SyncRuntimeScriptView(candidate);
                RefreshScripts();
                RefreshScriptSettings();
                SelectedNode = null;
                _logger.LogInformation(
                    "录制完成，已向当前脚本 {ScriptName} 追加 {ActionCount} 个操作，范围：{RecordingScope}。",
                    candidate.Name,
                    actions.Count,
                    RecordingScopeLabel());
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            _logger.LogError(
                exception,
                "录制步骤未能追加到当前脚本，原脚本保持不变。");
        }
        finally
        {
            try
            {
                await SetEditorReadOnlyAsync(false);
            }
            catch (Exception unlockException)
            {
                _logger.LogError(unlockException, "录制结束后恢复编辑器只读状态失败");
                failure ??= unlockException;
            }
            finally
            {
                ClearRecordingContext();
                _isRecordingFinalizing = false;
                RaiseRecordingState();
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private string ResolveRecordingTargetEndNodeId()
    {
        var endNodes = ActiveScript.Nodes
            .Where(node => node.Type == NodeTypes.End)
            .ToArray();
        var selectedEnd = SelectedNode?.Type == NodeTypes.End
            ? endNodes.FirstOrDefault(node => node.Id == SelectedNode.Id)
            : null;
        var target = selectedEnd ?? (endNodes.Length == 1 ? endNodes[0] : null);
        if (target is null)
            throw new InvalidOperationException(
                endNodes.Length == 0
                    ? "当前脚本没有结束节点，无法追加录制步骤。"
                    : "当前脚本有多个结束节点，请先在画布中选中要追加录制步骤的结束节点。");
        if (!ActiveScript.Edges.Any(edge => edge.Target == target.Id))
            throw new InvalidOperationException("选中的结束节点没有输入连接，无法追加录制步骤。");
        return target.Id;
    }

    private string RecordingScopeLabel() =>
        _recordingScope == InputRecordingScope.Window
            ? $"窗口：{_recordingWindow?.Title ?? "未知窗口"}"
            : "整个屏幕";

    private void ClearRecordingContext()
    {
        _recordingScope = InputRecordingScope.Screen;
        _recordingWindow = null;
        _recordingScriptId = null;
        _recordingTargetEndNodeId = null;
    }

    private async Task NewScriptAsync()
    {
        await ExportAsync();
        var name = CreateScriptName();
        var script = ProjectFactory.CreateScript(name, NextScriptId());
        _project.Scripts.Add(script);
        _activeScriptId = script.Id;
        SyncRuntimeScriptView(script);
        RefreshScripts();
        RefreshScriptSettings();
        SelectedNode = null;
        IsScriptPropertiesVisible = true;
        _dirty = true;
        await _editor.SendAsync("import_workflow", CreateEditorSnapshot(script));
    }

    private async Task CopyScriptAsync(ScriptListItem? item)
    {
        if (item is null) return;
        await ExportAsync();
        var script = _project.Scripts.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (script is null) return;
        _copiedScriptJson = JsonSerializer.Serialize(script, ProjectJson.Options);
        _copiedScriptSourceProjectId = _project.Project.Id;
        _copiedScriptSourceId = script.Id;
        RaiseCommandStates();
        _logger.LogInformation("已复制脚本 {ScriptName}", script.Name);
    }

    private async Task PasteScriptAsync()
    {
        if (string.IsNullOrWhiteSpace(_copiedScriptJson)) return;
        await ExportAsync();
        var script = JsonSerializer.Deserialize<AutomationScript>(_copiedScriptJson, ProjectJson.Options)
            ?? throw new InvalidDataException("复制的脚本内容为空。");
        var newScriptId = NextScriptId();
        RewriteCopiedScriptJumps(
            script,
            _copiedScriptSourceId,
            newScriptId,
            string.Equals(_copiedScriptSourceProjectId, _project.Project.Id, StringComparison.Ordinal));
        script.Id = newScriptId;
        script.Name = CreateScriptCopyName(script.Name);
        script.WindowScope ??= new();
        script.DefaultNodeDelayMs = Math.Clamp(script.DefaultNodeDelayMs, 0, 60000);

        // 先让编辑器确认新脚本可导入，再提交到项目集合，避免失败后覆盖副本内容。
        await ImportEditorWorkflowAsync(CreateEditorSnapshot(_project, script));
        _project.Scripts.Add(script);
        _activeScriptId = script.Id;
        SyncRuntimeScriptView(script);
        RefreshScripts();
        RefreshScriptSettings();
        SelectedNode = null;
        IsScriptPropertiesVisible = true;
        _dirty = true;
        _logger.LogInformation("已粘贴脚本副本 {ScriptName}", script.Name);
    }

    private async Task SelectScriptAsync(ScriptListItem? item)
    {
        if (item is null) return;
        if (item.Id == _activeScriptId)
        {
            SelectedNode = null;
            IsScriptPropertiesVisible = true;
            return;
        }
        await ExportAsync();
        var script = _project.Scripts.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (script is null) return;

        // 编辑器确认目标脚本导入成功后再切换活动状态，避免模型与画布错位。
        await ImportEditorWorkflowAsync(CreateEditorSnapshot(_project, script));
        _activeScriptId = script.Id;
        SyncRuntimeScriptView(script);
        RefreshScripts();
        RefreshScriptSettings();
        SelectedNode = null;
        IsScriptPropertiesVisible = true;
    }

    private async Task OpenScriptPropertiesAsync(ScriptListItem? item)
    {
        if (item is null) return;
        await SelectScriptAsync(item);
        SelectedNode = null;
        IsScriptPropertiesVisible = true;
        if (!IsPropertiesPaneVisible) TogglePropertiesPane();
    }

    private async Task DeleteScriptAsync(ScriptListItem? item)
    {
        if (item is null) return;
        if (_project.Scripts.Count <= 1) { _dialogs.ShowError("项目至少需要保留一个脚本。"); return; }
        await ExportAsync();
        var index = _project.Scripts.FindIndex(script => script.Id == item.Id);
        if (index < 0) return;
        var deletingActive = item.Id == _activeScriptId;
        _project.Scripts.RemoveAt(index);
        if (deletingActive)
        {
            var next = _project.Scripts[Math.Min(index, _project.Scripts.Count - 1)];
            _activeScriptId = next.Id;
            SyncRuntimeScriptView(next);
            RefreshScriptSettings();
            SelectedNode = null;
            IsScriptPropertiesVisible = false;
            await _editor.SendAsync("import_workflow", CreateEditorSnapshot(next));
        }
        RefreshScripts();
        _dirty = true;
    }

    private async Task<AutomationProject> ExportAsync()
    {
        var snapshot = await _editor.RequestAsync<WorkflowDocument>("request_workflow_export");
        var script = ActiveScript;
        script.Variables = snapshot.Variables;
        script.Nodes = snapshot.Nodes;
        script.Edges = snapshot.Edges;
        script.Groups = snapshot.Groups;
        _project.Project = snapshot.Project;
        SyncRuntimeScriptView(script);
        RefreshScripts();
        return _project;
    }

    private async Task SetEditorReadOnlyAsync(bool readOnly)
    {
        _editorReadOnlyRequested = readOnly;
        var response = await _editor.RequestAsync<EditorReadOnlyResponse>(
            "request_set_editor_readonly",
            new { readOnly });
        if (!response.Success || response.ReadOnly != readOnly)
            throw new InvalidOperationException(response.Error ?? "编辑器未确认只读状态。");
    }

    private Task ImportEditorWorkflowAsync(AutomationScript script) =>
        ImportEditorWorkflowAsync(CreateEditorSnapshot(script));

    private async Task ImportEditorWorkflowAsync(WorkflowDocument snapshot)
    {
        var response = await _editor.RequestAsync<EditorOperationResponse>(
            "request_import_workflow",
            snapshot);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "编辑器未确认工作流导入。");
    }

    private async Task EditSettingsAsync()
    {
        CapturePaneLayout();
        if (!_dialogs.EditSettings(Settings)) return;
        ApplyPaneVisibility();
        ApplyStepLayout();
        await _settingsStorage.SaveAsync(Settings, CancellationToken.None);
    }

    private AutomationScript ActiveScript =>
        _project.Scripts.First(script => script.Id == _activeScriptId);

    private void EnsureScriptModel(AutomationProject project)
    {
        if (project.Scripts.Count == 0)
        {
            project.Scripts.Add(new AutomationScript
            {
                Id = "1",
                Name = "主流程",
                Variables = project.Variables,
                Nodes = project.Nodes,
                Edges = project.Edges
            });
        }

        foreach (var script in project.Scripts)
        {
            script.WindowScope ??= new();
            script.DefaultNodeDelayMs = Math.Clamp(script.DefaultNodeDelayMs, 0, 60000);
        }

        if (string.IsNullOrWhiteSpace(_activeScriptId) || project.Scripts.All(script => script.Id != _activeScriptId))
            _activeScriptId = project.Scripts[0].Id;

        SyncRuntimeScriptView(project.Scripts.First(script => script.Id == _activeScriptId));
        RefreshScripts();
        RefreshScriptSettings();
    }

    private void SyncRuntimeScriptView(AutomationScript script)
    {
        _project.SetActiveScript(script);
    }

    private void RefreshScriptSettings()
    {
        var script = ActiveScript;
        script.WindowScope ??= new();
        _activeScriptName = script.Name;
        _scriptWindowScopeEnabled = script.WindowScope.Enabled;
        _scriptWindowMatchPattern = script.WindowScope.MatchPattern;
        _scriptWindowExcludePattern = script.WindowScope.ExcludePattern;
        _scriptInteractionMode = script.InteractionMode;
        _scriptOperationFlow = NormalizeOperationFlow(_scriptInteractionMode, script.WindowScope.ExecutionMode);
        script.WindowScope.ExecutionMode = _scriptOperationFlow;
        _scriptDefaultNodeDelayMs = Math.Clamp(script.DefaultNodeDelayMs, 0, 60000);
        Raise(nameof(ActiveScriptName));
        Raise(nameof(ActiveScriptId));
        Raise(nameof(UseWholeScreen));
        Raise(nameof(ScriptWindowScopeEnabled));
        Raise(nameof(ScriptWindowMatchPattern));
        Raise(nameof(ScriptWindowExcludePattern));
        Raise(nameof(ScriptInteractionMode));
        Raise(nameof(OperationFlowModes));
        Raise(nameof(ScriptOperationFlow));
        Raise(nameof(ScriptOperationFlowDescription));
        Raise(nameof(ScriptDefaultNodeDelayMs));
    }

    private static WindowExecutionMode NormalizeOperationFlow(
        ScriptInteractionModeValue interactionMode,
        WindowExecutionMode operationFlow)
    {
        if (interactionMode == ScriptInteractionModeValue.Background)
            return WindowExecutionMode.Parallel;

        return operationFlow is WindowExecutionMode.PseudoParallel or WindowExecutionMode.Serial
            ? operationFlow
            : WindowExecutionMode.Serial;
    }

    private void MarkScriptSettingsChanged()
    {
        if (_project.Scripts.Count == 0) return;
        SyncRuntimeScriptView(ActiveScript);
        _dirty = true;
    }

    private WorkflowDocument CreateEditorSnapshot(AutomationScript script) =>
        CreateEditorSnapshot(_project, script);

    private static WorkflowDocument CreateEditorSnapshot(
        AutomationProject project,
        AutomationScript script) => new()
    {
        SchemaVersion = project.SchemaVersion,
        Project = project.Project,
        Variables = script.Variables,
        Nodes = script.Nodes,
        Edges = script.Edges,
        Groups = script.Groups
    };

    private void RefreshScripts()
    {
        for (var index = 0; index < _project.Scripts.Count; index++)
        {
            var script = _project.Scripts[index];
            var currentIndex = -1;
            for (var candidateIndex = index; candidateIndex < Scripts.Count; candidateIndex++)
            {
                if (Scripts[candidateIndex].Id != script.Id) continue;
                currentIndex = candidateIndex;
                break;
            }

            ScriptListItem item;
            if (currentIndex < 0)
            {
                item = new ScriptListItem(script.Id, script.Name);
                Scripts.Insert(index, item);
            }
            else
            {
                if (currentIndex != index) Scripts.Move(currentIndex, index);
                item = Scripts[index];
            }

            item.Name = script.Name;
            item.IsActive = script.Id == _activeScriptId;
        }

        while (Scripts.Count > _project.Scripts.Count)
            Scripts.RemoveAt(Scripts.Count - 1);
    }

    private string CreateScriptName()
    {
        var index = _project.Scripts.Count + 1;
        var name = $"脚本 {index}";
        while (_project.Scripts.Any(script => string.Equals(script.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"脚本 {++index}";
        return name;
    }

    private static string CreateProjectCopyName(string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "未命名项目" : sourceName.Trim();
        return $"{baseName} 副本";
    }

    private string CreateScriptCopyName(string sourceName)
    {
        var source = string.IsNullOrWhiteSpace(sourceName) ? "未命名脚本" : sourceName.Trim();
        var baseName = $"{source} 副本";
        var name = baseName;
        var suffix = 2;
        while (_project.Scripts.Any(script => string.Equals(script.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
    }

    private static void RewriteCopiedScriptJumps(
        AutomationScript script,
        string? sourceScriptId,
        string newScriptId,
        bool isSameProject)
    {
        foreach (var node in script.Nodes.Where(node => node.Type == NodeTypes.Jump))
        {
            if (!node.Data.Parameters.TryGetValue("scriptId", out var targetValue)
                || targetValue.ValueKind != JsonValueKind.String)
                continue;
            var targetScriptId = targetValue.GetString();
            if (string.Equals(targetScriptId, sourceScriptId, StringComparison.Ordinal))
            {
                node.Data.Parameters["scriptId"] = ProjectJson.Element(newScriptId);
                continue;
            }

            if (!isSameProject)
                throw new InvalidOperationException(
                    "复制的脚本包含指向其他脚本的跳转，不能直接粘贴到另一个项目。请先复制整个项目，或移除跨脚本跳转。");
        }
    }

    private string NextScriptId()
    {
        var next = _project.Scripts
            .Select(script => int.TryParse(script.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return next.ToString(CultureInfo.InvariantCulture);
    }

    private string NormalizeScriptName(string value)
    {
        var baseName = string.IsNullOrWhiteSpace(value) ? "未命名脚本" : value.Trim();
        var name = baseName;
        var suffix = 2;
        while (_project.Scripts.Any(script => script.Id != _activeScriptId && string.Equals(script.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
    }

    private void OnNodeStarted(NodeExecutionEventArgs e) => Application.Current.Dispatcher.Invoke(() =>
    {
        // 某个并行轨道失败后，其他已排队轨道的迟到“开始”事件不能把错误状态改回运行中。
        if (_failedExecutionNodes.ContainsKey(e.Node)) return;
        CurrentNodeText = e.Node.Data.DisplayName;
        ObserveTask(_editor.SendAsync("highlight_node", new { nodeId = e.Node.Id }), "同步节点运行状态失败");
    });

    private void OnNodeCompleted(NodeExecutionEventArgs e) => Application.Current.Dispatcher.Invoke(() =>
    {
        // 后台并行时，同一节点可能由多个窗口轨道分别完成或失败。
        // 一旦任一轨道失败，本轮运行中不再允许其他轨道的成功状态覆盖红色错误状态。
        if (_failedExecutionNodes.ContainsKey(e.Node)) return;
        ObserveTask(_editor.SendAsync("mark_node_success", new { nodeId = e.Node.Id }), "同步节点成功状态失败");
    });

    private void OnNodeFailed(NodeExecutionEventArgs e)
    {
        _failedExecutionNodes.TryAdd(e.Node, 0);
        Application.Current.Dispatcher.Invoke(() =>
            ObserveTask(_editor.SendAsync("mark_node_error", new { nodeId = e.Node.Id }), "同步节点失败状态失败"));
    }

    private void RaiseCommandStates()
    {
        if (_deferCommandStateNotifications) return;
        foreach (var command in new[]
        {
            NewCommand, OpenCommand, SaveCommand, SaveAsCommand, OpenSampleCommand,
            RunCommand, RuntimeLaunchCommand, StartRecordingCommand, StopRecordingCommand,
            PauseCommand, ResumeCommand, StopCommand, AddNodeCommand, PickCoordinateCommand,
            SelectProgramPathCommand, NewScriptCommand, SelectScriptCommand, OpenScriptPropertiesCommand,
            DeleteScriptCommand, CopyProjectCommand, PasteProjectCommand, CopyScriptCommand,
            PasteScriptCommand, SettingsCommand
        })
            if (command is AsyncRelayCommand asyncCommand)
                asyncCommand.RaiseCanExecuteChanged();
    }
    private static string StateLabel(WorkflowExecutionState state) => state switch { WorkflowExecutionState.Running => "正在运行", WorkflowExecutionState.Paused => "已暂停", WorkflowExecutionState.Stopping => "正在停止", WorkflowExecutionState.Completed => "已完成", WorkflowExecutionState.Failed => "执行失败", _ => "未运行" };
    private static string DisplayValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private void InitializeCommands()
    {
        NewCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(NewAsync), _ => IsProjectEditingEnabled); OpenCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(OpenAsync), _ => IsProjectEditingEnabled);
        SaveCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(() => SaveAsync(false)), _ => IsProjectEditingEnabled); SaveAsCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(() => SaveAsync(true)), _ => IsProjectEditingEnabled);
        OpenSampleCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(OpenSampleAsync), _ => IsProjectEditingEnabled); RunCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(RunAsync), _ => IsProjectEditingEnabled);
        RuntimeLaunchCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(LaunchRuntimeAsync), _ => IsProjectEditingEnabled);
        StartRecordingCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(StartRecordingAsync), _ => IsProjectEditingEnabled);
        StopRecordingCommand = CreateAsyncCommand(_ => StopRecordingAsync(), _ => _inputRecorder.IsRecording && !_isRecordingFinalizing);
        PauseCommand = CreateAsyncCommand(_ => _runner.PauseAsync(), _ => _runner.State == WorkflowExecutionState.Running);
        ResumeCommand = CreateAsyncCommand(_ => _runner.ResumeAsync(), _ => _runner.State == WorkflowExecutionState.Paused);
        StopCommand = CreateAsyncCommand(_ => _runner.StopAsync(), _ => IsWorkflowActive());
        AddNodeCommand = CreateAsyncCommand(item => ExecuteProjectOperationAsync(() => AddNodeAsync(item as ToolboxItem)), _ => IsProjectEditingEnabled);
        PickCoordinateCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(PickCoordinateAsync), _ => IsProjectEditingEnabled && SelectedNode?.CanPickCoordinate == true);
        SelectProgramPathCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(SelectProgramPathAsync), _ => IsProjectEditingEnabled && SelectedNode?.CanPickProgramPath == true);
        NewScriptCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(NewScriptAsync), _ => IsProjectEditingEnabled);
        SelectScriptCommand = CreateAsyncCommand(
            item => ExecuteProjectOperationAsync(
                () => SelectScriptAsync(item as ScriptListItem),
                notifyBusyState: false),
            _ => IsProjectEditingEnabled,
            disableWhileRunning: false);
        OpenScriptPropertiesCommand = CreateAsyncCommand(
            item => ExecuteProjectOperationAsync(
                () => OpenScriptPropertiesAsync(item as ScriptListItem),
                notifyBusyState: false),
            _ => IsProjectEditingEnabled,
            disableWhileRunning: false);
        DeleteScriptCommand = CreateAsyncCommand(item => ExecuteProjectOperationAsync(() => DeleteScriptAsync(item as ScriptListItem)), _ => IsProjectEditingEnabled);
        CopyProjectCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(CopyProjectAsync), _ => IsProjectEditingEnabled);
        PasteProjectCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(PasteProjectAsync), _ => IsProjectEditingEnabled && !string.IsNullOrWhiteSpace(_copiedProjectJson));
        CopyScriptCommand = CreateAsyncCommand(item => ExecuteProjectOperationAsync(() => CopyScriptAsync(item as ScriptListItem)), item => IsProjectEditingEnabled && item is ScriptListItem);
        PasteScriptCommand = CreateAsyncCommand(_ => ExecuteProjectOperationAsync(PasteScriptAsync), _ => IsProjectEditingEnabled && !string.IsNullOrWhiteSpace(_copiedScriptJson));
        SettingsCommand = CreateAsyncCommand(_ => EditSettingsAsync(), _ => !IsRecording && !_isProjectOperationInProgress);
        HelpCommand = new RelayCommand(_ => _dialogs.ShowHelp());
        ToggleProjectFilesPaneCommand = new RelayCommand(_ => ToggleProjectFilesPane());
        ToggleScriptListPaneCommand = new RelayCommand(_ => ToggleScriptListPane());
        ToggleToolboxPaneCommand = new RelayCommand(_ => ToggleToolboxPane());
        TogglePropertiesPaneCommand = new RelayCommand(_ => TogglePropertiesPane());
        ToggleOutputPaneCommand = new RelayCommand(_ => ToggleOutputPane());
        NavigatePaneCommand = new RelayCommand(NavigatePane);
    }

    private AsyncRelayCommand CreateAsyncCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null,
        bool disableWhileRunning = true) =>
        new(
            execute,
            canExecute,
            exception => ReportOperationError(exception, "命令执行失败"),
            disableWhileRunning);

    private async Task ExecuteProjectOperationAsync(
        Func<Task> operation,
        bool notifyBusyState = true)
    {
        if (_isProjectOperationInProgress) return;
        _isProjectOperationInProgress = true;
        if (notifyBusyState)
            RaiseProjectOperationState();
        else
        {
            _deferCommandStateNotifications = true;
            _isWorkspaceInputBlocked = true;
            Raise(nameof(IsWorkspaceInputEnabled));
        }
        try { await operation(); }
        finally
        {
            _isProjectOperationInProgress = false;
            if (notifyBusyState)
            {
                RaiseProjectOperationState();
            }
            else
            {
                _isWorkspaceInputBlocked = false;
                Raise(nameof(IsWorkspaceInputEnabled));
                _deferCommandStateNotifications = false;
                RaiseCommandStates();
            }
        }
    }

    private void RaiseProjectOperationState()
    {
        Raise(nameof(IsProjectEditingEnabled));
        Raise(nameof(IsEditorInteractionEnabled));
        RaiseCommandStates();
    }

    private void SubscribeToEvents(GameFlowLogProvider logProvider)
    {
        _editor.MessageReceived += OnEditorMessage;
        _runner.NodeStarted += (_, args) => OnNodeStarted(args);
        _runner.NodeCompleted += (_, args) => OnNodeCompleted(args);
        _runner.NodeFailed += (_, args) => OnNodeFailed(args);
        _runner.StateChanged += OnWorkflowStateChanged;
        _inputRecorder.ActionRecorded += OnRecordedAction;
        _inputRecorder.StopRequested += OnRecordingStopRequested;
        logProvider.EntryAdded += OnLogEntryAdded;
    }

    private void RestoreLogEntries(GameFlowLogProvider logProvider)
    {
        foreach (var entry in logProvider.Entries) Logs.Add(entry);
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateChangedEventArgs args) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            StateText = StateLabel(args.Current);
            Raise(nameof(IsProjectEditingEnabled));
            Raise(nameof(IsEditorInteractionEnabled));
            RaiseCommandStates();
        });

    private void OnRecordedAction(object? sender, RecordedInputActionEventArgs args)
    {
        _ = args;
        Interlocked.Increment(ref _recordedActionCount);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (Interlocked.Exchange(ref _recordingStatusNotificationPending, 1) != 0) return;
        _ = dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _recordingStatusNotificationPending, 0);
            Raise(nameof(RecordingStatusText));
        });
    }

    private void OnRecordingStopRequested(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(() =>
        {
            if (StopRecordingCommand.CanExecute(null))
                StopRecordingCommand.Execute(null);
        });
    }

    private void RaiseRecordingState()
    {
        Raise(nameof(IsRecording));
        Raise(nameof(IsEditorInteractionEnabled));
        Raise(nameof(RecordingStatusText));
        Raise(nameof(IsProjectEditingEnabled));
        RaiseCommandStates();
    }

    private void OnLogEntryAdded(object? sender, DisplayLogEntry entry) =>
        Application.Current.Dispatcher.Invoke(() => { Logs.Add(entry); while (Logs.Count > 1000) Logs.RemoveAt(0); });

    private void ObserveTask(Task task, string operation) => _ = ObserveTaskAsync(task, operation);

    private async Task ObserveTaskAsync(Task task, string operation)
    {
        try { await task; }
        catch (Exception exception) { _logger.LogError(exception, "{Operation}", operation); }
    }

    private void StopActiveWorkflow()
    {
        if (IsWorkflowActive()) _runner.StopAsync().GetAwaiter().GetResult();
    }

    private void StopActiveRecording()
    {
        if (!_inputRecorder.IsRecording) return;
        try { _ = _inputRecorder.Stop(); }
        catch (Exception exception) { _logger.LogError(exception, "关闭前停止操作录制失败"); }
    }

    private bool IsWorkflowActive() => _runner.State is WorkflowExecutionState.Running or WorkflowExecutionState.Paused or WorkflowExecutionState.Stopping;

    private void CaptureWindowSettings()
    {
        var window = Application.Current.MainWindow;
        Settings.WindowWidth = window.Width; Settings.WindowHeight = window.Height;
        Settings.WindowLeft = window.Left; Settings.WindowTop = window.Top;
    }

    private void ApplyPaneLayout()
    {
        _lastToolboxPaneWidth = new GridLength(ClampPaneSize(Settings.ToolboxPaneWidth, 220, 170, 480));
        _lastPropertiesPaneWidth = new GridLength(ClampPaneSize(Settings.PropertiesPaneWidth, 320, 220, 560));
        _lastOutputPaneHeight = new GridLength(ClampPaneSize(Settings.OutputPaneHeight, 220, 120, 520));
        _lastProjectFilesPaneWidth = new GridLength(210);
        _lastScriptListPaneWidth = new GridLength(220);
        ApplyPaneVisibility();
    }

    private void RaiseLeftSidebarLayout()
    {
        Raise(nameof(LeftSidebarWidth));
        Raise(nameof(LeftSidebarMinWidth));
        Raise(nameof(IsLeftSidebarVisible));
    }

    private void ApplyPaneVisibility()
    {
        var startupSidebarPanel = Settings.IsProjectFilesPaneVisible
            ? "project"
            : Settings.IsScriptListPaneVisible
                ? "scripts"
                : Settings.IsToolboxPaneVisible ? "toolbox" : string.Empty;
        SetSidebarPanel(startupSidebarPanel);
        IsPropertiesPaneVisible = Settings.IsPropertiesPaneVisible;
        IsOutputPaneVisible = Settings.IsOutputPaneVisible;
        PropertiesPaneWidth = IsPropertiesPaneVisible ? _lastPropertiesPaneWidth : new GridLength(0);
        OutputPaneHeight = IsOutputPaneVisible ? _lastOutputPaneHeight : new GridLength(0);
        Settings.IsProjectFilesPaneVisible = IsProjectFilesPaneVisible;
        Settings.IsScriptListPaneVisible = IsScriptListPaneVisible;
        Settings.IsToolboxPaneVisible = IsToolboxPaneVisible;
    }

    private void ApplyStepLayout() =>
        IsHorizontalStepLayout = string.Equals(Settings.StepCategoryLayout, "horizontal", StringComparison.OrdinalIgnoreCase);

    private void CapturePaneLayout()
    {
        if (IsProjectFilesPaneVisible && ProjectFilesPaneWidth.Value > 0) _lastProjectFilesPaneWidth = ProjectFilesPaneWidth;
        if (IsScriptListPaneVisible && ScriptListPaneWidth.Value > 0) _lastScriptListPaneWidth = ScriptListPaneWidth;
        if (IsToolboxPaneVisible && ToolboxPaneWidth.Value > 0) _lastToolboxPaneWidth = ToolboxPaneWidth;
        if (IsPropertiesPaneVisible && PropertiesPaneWidth.Value > 0) _lastPropertiesPaneWidth = PropertiesPaneWidth;
        if (IsOutputPaneVisible && OutputPaneHeight.Value > 0) _lastOutputPaneHeight = OutputPaneHeight;
        Settings.ToolboxPaneWidth = _lastToolboxPaneWidth.Value;
        Settings.PropertiesPaneWidth = _lastPropertiesPaneWidth.Value;
        Settings.OutputPaneHeight = _lastOutputPaneHeight.Value;
        Settings.IsProjectFilesPaneVisible = IsProjectFilesPaneVisible;
        Settings.IsScriptListPaneVisible = IsScriptListPaneVisible;
        Settings.IsToolboxPaneVisible = IsToolboxPaneVisible;
        Settings.IsPropertiesPaneVisible = IsPropertiesPaneVisible;
        Settings.IsOutputPaneVisible = IsOutputPaneVisible;
    }

    private void NavigatePane(object? parameter)
    {
        if (parameter is not string pane) return;

        if (pane is "project" or "scripts" or "toolbox")
        {
            SetSidebarPanel(pane);
            CapturePaneLayout();
            PersistSettings();
            return;
        }

        if (pane == "properties") TogglePropertiesPane();
        else if (pane == "output") ToggleOutputPane();
    }

    private void SetSidebarPanel(string pane)
    {
        if (pane is not ("" or "project" or "scripts" or "toolbox")) return;

        // 左侧活动栏一次只显示一个内容面板。
        IsProjectFilesPaneVisible = pane == "project";
        IsScriptListPaneVisible = pane == "scripts";
        IsToolboxPaneVisible = pane == "toolbox";
        ProjectFilesPaneWidth = IsProjectFilesPaneVisible ? _lastProjectFilesPaneWidth : new GridLength(0);
        ScriptListPaneWidth = IsScriptListPaneVisible ? _lastScriptListPaneWidth : new GridLength(0);
        ToolboxPaneWidth = IsToolboxPaneVisible ? _lastToolboxPaneWidth : new GridLength(0);
        ActiveNavigationItem = pane;
    }

    private void ToggleProjectFilesPane()
    {
        if (IsProjectFilesPaneVisible)
        {
            if (ProjectFilesPaneWidth.Value > 0) _lastProjectFilesPaneWidth = ProjectFilesPaneWidth;
            SetSidebarPanel(string.Empty);
        }
        else SetSidebarPanel("project");
        CapturePaneLayout(); PersistSettings();
    }

    private void ToggleScriptListPane()
    {
        if (IsScriptListPaneVisible)
        {
            if (ScriptListPaneWidth.Value > 0) _lastScriptListPaneWidth = ScriptListPaneWidth;
            SetSidebarPanel(string.Empty);
        }
        else SetSidebarPanel("scripts");
        CapturePaneLayout(); PersistSettings();
    }

    private void ToggleToolboxPane()
    {
        if (IsToolboxPaneVisible)
        {
            if (ToolboxPaneWidth.Value > 0) _lastToolboxPaneWidth = ToolboxPaneWidth;
            SetSidebarPanel(string.Empty);
        }
        else SetSidebarPanel("toolbox");
        CapturePaneLayout(); PersistSettings();
    }

    private void TogglePropertiesPane()
    {
        if (IsPropertiesPaneVisible)
        {
            if (PropertiesPaneWidth.Value > 0) _lastPropertiesPaneWidth = PropertiesPaneWidth;
            PropertiesPaneWidth = new GridLength(0);
            IsPropertiesPaneVisible = false;
        }
        else
        {
            PropertiesPaneWidth = _lastPropertiesPaneWidth;
            IsPropertiesPaneVisible = true;
        }
        CapturePaneLayout(); PersistSettings();
    }

    private void ToggleOutputPane()
    {
        if (IsOutputPaneVisible)
        {
            if (OutputPaneHeight.Value > 0) _lastOutputPaneHeight = OutputPaneHeight;
            OutputPaneHeight = new GridLength(0);
            IsOutputPaneVisible = false;
        }
        else
        {
            OutputPaneHeight = _lastOutputPaneHeight;
            IsOutputPaneVisible = true;
        }
        CapturePaneLayout(); PersistSettings();
    }

    private static double ClampPaneSize(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum ? value : fallback;

    private void PersistSettings()
    {
        var save = _settingsStorage.SaveAsync(Settings, CancellationToken.None);
        lock (_settingsSaveSync)
            _settingsSaveChain = Task.WhenAll(_settingsSaveChain, save);
    }

    private void WaitForPendingSettingsSaves()
    {
        Task pending;
        lock (_settingsSaveSync)
            pending = _settingsSaveChain;

        try
        {
            pending.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "退出前保存设置失败");
        }
    }

    private void ApplyLoadedProject(
        AutomationProject project,
        string path,
        bool rememberPath,
        bool markDirty = false)
    {
        _project = project; _activeScriptId = string.Empty; EnsureScriptModel(_project);
        _currentPath = rememberPath ? path : null; _dirty = markDirty; Raise(nameof(CurrentFileName)); Raise(nameof(CurrentPathText));
        ProjectName = project.Project.Name; SelectedNode = null;
    }

    private string? ResolveSavePath(bool saveAs) => saveAs || _currentPath is null ? _dialogs.SaveProject(_currentPath) : _currentPath;

    private void ApplySavedProject(AutomationProject project, string path)
    {
        _project = project; EnsureScriptModel(_project);
        _currentPath = path; _dirty = false; Raise(nameof(CurrentFileName)); Raise(nameof(CurrentPathText)); ProjectName = project.Project.Name;
    }

    private async Task<bool> ValidateForRunAsync(AutomationProject project)
    {
        var validation = _validator.Validate(project);
        if (validation.IsValid) return true;
        foreach (var error in validation.Errors)
        {
            _logger.LogError("验证失败：{Message}", error.Message);
            if (error.NodeId is not null) await _editor.SendAsync("mark_node_error", new { nodeId = error.NodeId });
        }
        _dialogs.ShowError("工作流验证失败，请查看日志和红色节点。");
        return false;
    }

    private async Task ExecuteWorkflowAsync(AutomationProject project)
    {
        await SetEditorReadOnlyAsync(true);
        _executionTimer.Restart();
        await _runner.RunAsync(project, CancellationToken.None);
    }

    private async Task FinishRunAsync()
    {
        _executionTimer.Stop(); ElapsedText = $"{_executionTimer.ElapsedMilliseconds} ms";
        await SetEditorReadOnlyAsync(false);
        RaiseCommandStates();
    }

    private void ReportOperationError(Exception exception, string operation)
    {
        _logger.LogError(exception, "{Operation}", operation);
        _dialogs.ShowError(exception.Message);
    }

    private sealed record EditorOperationResponse(bool Success, string? Error);
    private sealed record EditorReadOnlyResponse(bool Success, bool ReadOnly, string? Error);
}
