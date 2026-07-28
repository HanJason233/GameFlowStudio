using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GameFlowStudio.Contracts.Enums;
using GameFlowStudio.Contracts.Events;
using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<MainViewModel> _logger;
    private readonly object _settingsSaveSync = new();
    private Task _settingsSaveChain = Task.CompletedTask;
    private AutomationProject _project = ProjectFactory.CreateNew();
    private string _activeScriptId = string.Empty;
    private string? _currentPath;
    private string _projectName = "未命名项目";
    private string _stateText = "未运行";
    private string _currentNodeText = "—";
    private string _elapsedText = "—";
    private bool _dirty;
    private bool _runtimeLaunchInProgress;
    private SelectedNodeViewModel? _selectedNode;
    private bool _suppressSelectedNodeUpdates;
    private readonly Stopwatch _executionTimer = new();
    private GridLength _toolboxPaneWidth = new(220);
    private GridLength _propertiesPaneWidth = new(320);
    private GridLength _outputPaneHeight = new(220);
    private GridLength _lastToolboxPaneWidth = new(220);
    private GridLength _lastPropertiesPaneWidth = new(320);
    private GridLength _lastOutputPaneHeight = new(220);
    private GridLength _stepsPaneHeight = new(220);
    private GridLength _lastStepsPaneHeight = new(220);
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
    private string _scriptWindowTitlePattern = ".*";
    private WindowExecutionMode _scriptWindowExecutionMode = WindowExecutionMode.FirstOnly;
    private string _activeScriptName = string.Empty;
    private int _scriptDefaultNodeDelayMs = AutomationScript.DefaultNodeDelayMilliseconds;

    public MainViewModel(IEditorBridge editor, IWorkflowRunner runner, IWorkflowValidator validator, IProjectStorageService storage,
        ISettingsStorageService settingsStorage, IFileDialogService dialogs, ICoordinatePickerService coordinatePicker,
        IColorPickerService colorPicker,
        IRectanglePickerService rectanglePicker, IImageCaptureService imageCapture,
        ICoordinatePickModeService coordinatePickMode,
        IWindowSelectionService windowSelection, IWindowAutomationService windows, IRelativeCoordinatePickerService relativeCoordinatePicker,
        IRuntimeProcessLauncher runtimeLauncher, GameFlowLogProvider logProvider, ILogger<MainViewModel> logger)
    {
        _editor = editor; _runner = runner; _validator = validator; _storage = storage; _settingsStorage = settingsStorage;
        _dialogs = dialogs; _coordinatePicker = coordinatePicker; _colorPicker = colorPicker; _rectanglePicker = rectanglePicker; _imageCapture = imageCapture;
        _coordinatePickMode = coordinatePickMode; _windowSelection = windowSelection; _windows = windows; _relativeCoordinatePicker = relativeCoordinatePicker; _runtimeLauncher = runtimeLauncher; _logger = logger;
        ToolboxGroups = NodeCatalog.Groups;
        EnsureScriptModel(_project);
        InitializeCommands();
        SubscribeToEvents(logProvider);
        RestoreLogEntries(logProvider);
    }

    public ObservableCollection<DisplayLogEntry> Logs { get; } = [];
    public ObservableCollection<ProjectFileItem> RecentProjectFiles { get; } = [];
    public ObservableCollection<ScriptListItem> Scripts { get; } = [];
    public IReadOnlyList<WindowExecutionModeOption> WindowExecutionModes { get; } =
    [
        new(WindowExecutionMode.FirstOnly, "只操作第一个窗口", "匹配多个窗口时，只激活并操作第一个窗口。"),
        new(WindowExecutionMode.WindowPriority, "窗口优先", "先完整执行窗口 1 的流程，再完整执行窗口 2 的流程。"),
        new(WindowExecutionMode.ActionPriority, "动作优先", "每一轮让所有窗口各执行一个当前动作，再进入下一轮。")
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
    public bool IsProjectEditingEnabled => !IsWorkflowActive();
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
    public string ScriptWindowTitlePattern
    {
        get => _scriptWindowTitlePattern;
        set { if (Set(ref _scriptWindowTitlePattern, value)) { ActiveScript.WindowScope.TitlePattern = value; MarkScriptSettingsChanged(); } }
    }
    public WindowExecutionMode ScriptWindowExecutionMode
    {
        get => _scriptWindowExecutionMode;
        set
        {
            if (!Set(ref _scriptWindowExecutionMode, value)) return;
            ActiveScript.WindowScope.ExecutionMode = value;
            Raise(nameof(ScriptWindowExecutionModeDescription));
            MarkScriptSettingsChanged();
        }
    }
    public string ScriptWindowExecutionModeDescription =>
        WindowExecutionModes.First(mode => mode.Value == ScriptWindowExecutionMode).Description;
    public int ScriptDefaultNodeDelayMs
    {
        get => _scriptDefaultNodeDelayMs;
        set
        {
            var normalized = Math.Clamp(value, 0, 60000);
            if (Set(ref _scriptDefaultNodeDelayMs, normalized)) { ActiveScript.DefaultNodeDelayMs = normalized; MarkScriptSettingsChanged(); }
        }
    }
    public GridLength ToolboxPaneWidth { get => _toolboxPaneWidth; set => Set(ref _toolboxPaneWidth, value); }
    public GridLength PropertiesPaneWidth { get => _propertiesPaneWidth; set => Set(ref _propertiesPaneWidth, value); }
    public GridLength OutputPaneHeight { get => _outputPaneHeight; set => Set(ref _outputPaneHeight, value); }
    public GridLength StepsPaneHeight { get => _stepsPaneHeight; set => Set(ref _stepsPaneHeight, value); }
    public GridLength ProjectFilesPaneWidth { get => _projectFilesPaneWidth; set => Set(ref _projectFilesPaneWidth, value); }
    public GridLength ScriptListPaneWidth { get => _scriptListPaneWidth; set => Set(ref _scriptListPaneWidth, value); }
    public bool IsProjectFilesPaneVisible { get => _isProjectFilesPaneVisible; private set { if (Set(ref _isProjectFilesPaneVisible, value)) Raise(nameof(ProjectFilesPaneMinWidth)); } }
    public bool IsScriptListPaneVisible { get => _isScriptListPaneVisible; private set { if (Set(ref _isScriptListPaneVisible, value)) Raise(nameof(ScriptListPaneMinWidth)); } }
    public bool IsToolboxPaneVisible { get => _isToolboxPaneVisible; private set { if (Set(ref _isToolboxPaneVisible, value)) { Raise(nameof(ToolboxPaneMinWidth)); Raise(nameof(StepsPaneMinHeight)); } } }
    public bool IsPropertiesPaneVisible { get => _isPropertiesPaneVisible; private set { if (Set(ref _isPropertiesPaneVisible, value)) Raise(nameof(PropertiesPaneMinWidth)); } }
    public bool IsOutputPaneVisible { get => _isOutputPaneVisible; private set { if (Set(ref _isOutputPaneVisible, value)) Raise(nameof(OutputPaneMinHeight)); } }
    public bool IsHorizontalStepLayout { get => _isHorizontalStepLayout; private set => Set(ref _isHorizontalStepLayout, value); }
    public double ToolboxPaneMinWidth => IsToolboxPaneVisible ? 170 : 0;
    public double PropertiesPaneMinWidth => IsPropertiesPaneVisible ? 220 : 0;
    public double OutputPaneMinHeight => IsOutputPaneVisible ? 120 : 0;
    public double StepsPaneMinHeight => IsToolboxPaneVisible ? 170 : 0;
    public double ProjectFilesPaneMinWidth => IsProjectFilesPaneVisible ? 180 : 0;
    public double ScriptListPaneMinWidth => IsScriptListPaneVisible ? 190 : 0;
    public event EventHandler? HotkeysChanged;

    public ICommand NewCommand { get; private set; } = null!; public ICommand OpenCommand { get; private set; } = null!; public ICommand SaveCommand { get; private set; } = null!;
    public ICommand SaveAsCommand { get; private set; } = null!; public ICommand OpenSampleCommand { get; private set; } = null!; public ICommand RunCommand { get; private set; } = null!;
    public ICommand RuntimeLaunchCommand { get; private set; } = null!;
    public ICommand PauseCommand { get; private set; } = null!; public ICommand ResumeCommand { get; private set; } = null!; public ICommand StopCommand { get; private set; } = null!;
    public ICommand AddNodeCommand { get; private set; } = null!; public ICommand PickCoordinateCommand { get; private set; } = null!; public ICommand SelectProgramPathCommand { get; private set; } = null!;
    public ICommand OpenRecentProjectCommand { get; private set; } = null!;
    public ICommand NewScriptCommand { get; private set; } = null!;
    public ICommand SelectScriptCommand { get; private set; } = null!;
    public ICommand OpenScriptPropertiesCommand { get; private set; } = null!;
    public ICommand DeleteScriptCommand { get; private set; } = null!;
    public ICommand ToggleProjectFilesPaneCommand { get; private set; } = null!;
    public ICommand ToggleScriptListPaneCommand { get; private set; } = null!;
    public ICommand SettingsCommand { get; private set; } = null!;
    public ICommand ToggleToolboxPaneCommand { get; private set; } = null!;
    public ICommand TogglePropertiesPaneCommand { get; private set; } = null!;
    public ICommand ToggleOutputPaneCommand { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Settings = await _settingsStorage.LoadAsync(CancellationToken.None);
        RefreshRecentProjectFiles();
        ApplyPaneLayout();
        ApplyStepLayout();
        ApplyTheme();
        ProjectName = _project.Project.Name;
        if (_editor.IsReady) await _editor.SendAsync("initialize_editor", CreateEditorSnapshot(ActiveScript));
    }

    public Task LoadStartupProjectAsync(string path) => LoadProjectAsync(path, true);

    public bool CanClose()
    {
        if (!_runtimeLaunchInProgress && _dirty && !_dialogs.ConfirmDiscardChanges()) return false;
        StopActiveWorkflow();
        CaptureWindowSettings();
        CapturePaneLayout();
        PersistSettings();
        WaitForPendingSettingsSaves();
        return true;
    }

    public void HandleHotkey(int id)
    {
        var command = id switch { 1 => RunCommand, 2 when _runner.State == WorkflowExecutionState.Paused => ResumeCommand, 2 => PauseCommand, 3 => StopCommand, _ => null };
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
    public void ReportHotkeyError(Exception exception) { _logger.LogError(exception, "全局快捷键注册失败"); _dialogs.ShowError(exception.Message); }

    private async void OnEditorMessage(object? sender, EditorMessage message)
    {
        try
        {
            switch (message.Type)
            {
                  case "editor_ready": await _editor.SendAsync("initialize_editor", CreateEditorSnapshot(ActiveScript)); break;
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
                var value = property.Name == "coordinateMode"
                    ? NormalizeCoordinateMode(DisplayValue(property.Value))
                    : DisplayValue(property.Value);
                selected.Parameters.Add(new(property.Name, value, property.Value.ValueKind, OnSelectedNodeEditorChanged));
            }
        }
        if (selected.SupportsCoordinateMode && !selected.Parameters.Any(parameter => parameter.Name == "coordinateMode"))
            selected.Parameters.Insert(0, new("coordinateMode", "followScript", JsonValueKind.String, OnSelectedNodeEditorChanged));
        if (selected.IsDetectorCondition)
        {
            var confidenceName = selected.Type == NodeTypes.ColorCondition ? "minMatchRatio" : "matchThreshold";
            var defaultConfidence = selected.Type == NodeTypes.ColorCondition ? "1" : selected.Type == NodeTypes.OcrCondition ? "0.8" : "0.95";
            if (!selected.Parameters.Any(parameter => parameter.Name == confidenceName))
                selected.Parameters.Add(new(confidenceName, defaultConfidence, JsonValueKind.Number, OnSelectedNodeEditorChanged));
        }
        SelectedNode = selected;
    }

    private static string NormalizeCoordinateMode(string value) => value.ToLowerInvariant() switch
    {
        "screen" => "absolute",
        "windowrelative" => "relative",
        _ => value
    };

    private void OnSelectedNodeEditorChanged()
    {
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

    private async Task OpenAsync()
    {
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;
        var path = _dialogs.OpenProject(Settings.DefaultProjectDirectory); if (path is null) return;
        await LoadProjectAsync(path);
    }

    private async Task OpenRecentAsync(ProjectFileItem? item)
    {
        if (item is null) return;
        if (!File.Exists(item.Path)) { _dialogs.ShowError("找不到项目文件。请重新打开或移除该记录。"); return; }
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;
        await LoadProjectAsync(item.Path);
    }

    private async Task OpenSampleAsync()
    {
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return;
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "basic-example.gflow");
        if (!File.Exists(path)) { _dialogs.ShowError("找不到示例项目文件。"); return; }
        await LoadProjectAsync(path, false);
    }

    private async Task LoadProjectAsync(string path, bool rememberPath = true)
    {
        try
        {
            var project = await _storage.LoadAsync(path, CancellationToken.None);
            ApplyLoadedProject(project, path, rememberPath);
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
            await _runtimeLauncher.LaunchAsync(project, CancellationToken.None);
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
        _activeScriptId = script.Id;
        SyncRuntimeScriptView(script);
        RefreshScripts();
        RefreshScriptSettings();
        SelectedNode = null;
        IsScriptPropertiesVisible = true;
        await _editor.SendAsync("import_workflow", CreateEditorSnapshot(script));
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
        _project.Project = snapshot.Project;
        SyncRuntimeScriptView(script);
        RefreshScripts();
        return _project;
    }
    private async Task EditSettingsAsync()
    {
        CapturePaneLayout();
        if (!_dialogs.EditSettings(Settings)) return;
        ApplyPaneVisibility();
        ApplyStepLayout();
        ApplyTheme();
        await _settingsStorage.SaveAsync(Settings, CancellationToken.None);
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddRecent(string path)
    {
        UpdateRecentProjects(path);
        RefreshRecentProjectFiles();
        PersistSettings();
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
        _scriptWindowTitlePattern = script.WindowScope.TitlePattern;
        _scriptWindowExecutionMode = script.WindowScope.ExecutionMode;
        _scriptDefaultNodeDelayMs = Math.Clamp(script.DefaultNodeDelayMs, 0, 60000);
        Raise(nameof(ActiveScriptName));
        Raise(nameof(ActiveScriptId));
        Raise(nameof(UseWholeScreen));
        Raise(nameof(ScriptWindowScopeEnabled));
        Raise(nameof(ScriptWindowTitlePattern));
        Raise(nameof(ScriptWindowExecutionMode));
        Raise(nameof(ScriptWindowExecutionModeDescription));
        Raise(nameof(ScriptDefaultNodeDelayMs));
    }

    private void MarkScriptSettingsChanged()
    {
        if (_project.Scripts.Count == 0) return;
        SyncRuntimeScriptView(ActiveScript);
        _dirty = true;
    }

    private WorkflowDocument CreateEditorSnapshot(AutomationScript script) => new()
    {
        SchemaVersion = _project.SchemaVersion,
        Project = _project.Project,
        Variables = script.Variables,
        Nodes = script.Nodes,
        Edges = script.Edges
    };

    private void RefreshScripts()
    {
        Scripts.Clear();
        foreach (var script in _project.Scripts)
            Scripts.Add(new ScriptListItem(script.Id, script.Name, $"{script.Nodes.Count} 个节点") { IsActive = script.Id == _activeScriptId });
    }

    private string CreateScriptName()
    {
        var index = _project.Scripts.Count + 1;
        var name = $"脚本 {index}";
        while (_project.Scripts.Any(script => string.Equals(script.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"脚本 {++index}";
        return name;
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

    private void RefreshRecentProjectFiles()
    {
        RecentProjectFiles.Clear();
        foreach (var path in Settings.RecentProjects.Where(File.Exists))
            RecentProjectFiles.Add(new(Path.GetFileName(path), path));
    }

    private void OnNodeStarted(NodeExecutionEventArgs e) => Application.Current.Dispatcher.Invoke(() =>
    {
        CurrentNodeText = e.Node.Data.DisplayName;
        ObserveTask(_editor.SendAsync("highlight_node", new { nodeId = e.Node.Id }), "同步节点运行状态失败");
    });
    private void RaiseCommandStates()
    {
        foreach (var command in new[]
        {
            NewCommand, OpenCommand, OpenRecentProjectCommand, SaveCommand, SaveAsCommand, OpenSampleCommand,
            RunCommand, RuntimeLaunchCommand, PauseCommand, ResumeCommand, StopCommand, AddNodeCommand, PickCoordinateCommand,
            SelectProgramPathCommand, NewScriptCommand, SelectScriptCommand, OpenScriptPropertiesCommand,
            DeleteScriptCommand
        })
            if (command is AsyncRelayCommand asyncCommand)
                asyncCommand.RaiseCanExecuteChanged();
    }
    private static string StateLabel(WorkflowExecutionState state) => state switch { WorkflowExecutionState.Running => "正在运行", WorkflowExecutionState.Paused => "已暂停", WorkflowExecutionState.Stopping => "正在停止", WorkflowExecutionState.Completed => "已完成", WorkflowExecutionState.Failed => "执行失败", _ => "未运行" };
    private static string DisplayValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private void InitializeCommands()
    {
        NewCommand = CreateAsyncCommand(_ => NewAsync(), _ => IsProjectEditingEnabled); OpenCommand = CreateAsyncCommand(_ => OpenAsync(), _ => IsProjectEditingEnabled); OpenRecentProjectCommand = CreateAsyncCommand(item => OpenRecentAsync(item as ProjectFileItem), _ => IsProjectEditingEnabled);
        SaveCommand = CreateAsyncCommand(_ => SaveAsync(false), _ => IsProjectEditingEnabled); SaveAsCommand = CreateAsyncCommand(_ => SaveAsync(true), _ => IsProjectEditingEnabled);
        OpenSampleCommand = CreateAsyncCommand(_ => OpenSampleAsync(), _ => IsProjectEditingEnabled); RunCommand = CreateAsyncCommand(_ => RunAsync(), _ => IsProjectEditingEnabled);
        RuntimeLaunchCommand = CreateAsyncCommand(_ => LaunchRuntimeAsync(), _ => IsProjectEditingEnabled);
        PauseCommand = CreateAsyncCommand(_ => _runner.PauseAsync(), _ => _runner.State == WorkflowExecutionState.Running);
        ResumeCommand = CreateAsyncCommand(_ => _runner.ResumeAsync(), _ => _runner.State == WorkflowExecutionState.Paused);
        StopCommand = CreateAsyncCommand(_ => _runner.StopAsync(), _ => IsWorkflowActive());
        AddNodeCommand = CreateAsyncCommand(item => AddNodeAsync(item as ToolboxItem), _ => IsProjectEditingEnabled);
        PickCoordinateCommand = CreateAsyncCommand(_ => PickCoordinateAsync(), _ => IsProjectEditingEnabled && SelectedNode?.CanPickCoordinate == true);
        SelectProgramPathCommand = CreateAsyncCommand(_ => SelectProgramPathAsync(), _ => IsProjectEditingEnabled && SelectedNode?.CanPickProgramPath == true);
        NewScriptCommand = CreateAsyncCommand(_ => NewScriptAsync(), _ => IsProjectEditingEnabled);
        SelectScriptCommand = CreateAsyncCommand(item => SelectScriptAsync(item as ScriptListItem), _ => IsProjectEditingEnabled);
        OpenScriptPropertiesCommand = CreateAsyncCommand(item => OpenScriptPropertiesAsync(item as ScriptListItem), _ => IsProjectEditingEnabled);
        DeleteScriptCommand = CreateAsyncCommand(item => DeleteScriptAsync(item as ScriptListItem), _ => IsProjectEditingEnabled);
        SettingsCommand = CreateAsyncCommand(_ => EditSettingsAsync());
        ToggleProjectFilesPaneCommand = new RelayCommand(_ => ToggleProjectFilesPane());
        ToggleScriptListPaneCommand = new RelayCommand(_ => ToggleScriptListPane());
        ToggleToolboxPaneCommand = new RelayCommand(_ => ToggleToolboxPane());
        TogglePropertiesPaneCommand = new RelayCommand(_ => TogglePropertiesPane());
        ToggleOutputPaneCommand = new RelayCommand(_ => ToggleOutputPane());
    }

    private AsyncRelayCommand CreateAsyncCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null) =>
        new(execute, canExecute, exception => ReportOperationError(exception, "命令执行失败"));

    private void SubscribeToEvents(GameFlowLogProvider logProvider)
    {
        _editor.MessageReceived += OnEditorMessage;
        _runner.NodeStarted += (_, args) => OnNodeStarted(args);
        _runner.NodeCompleted += (_, args) => ObserveTask(_editor.SendAsync("mark_node_success", new { nodeId = args.Node.Id }), "同步节点成功状态失败");
        _runner.NodeFailed += (_, args) => ObserveTask(_editor.SendAsync("mark_node_error", new { nodeId = args.Node.Id }), "同步节点失败状态失败");
        _runner.StateChanged += OnWorkflowStateChanged;
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
            RaiseCommandStates();
        });

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
        _lastStepsPaneHeight = new GridLength(220);
        _lastProjectFilesPaneWidth = new GridLength(210);
        _lastScriptListPaneWidth = new GridLength(220);
        ApplyPaneVisibility();
    }

    private void ApplyPaneVisibility()
    {
        IsProjectFilesPaneVisible = Settings.IsProjectFilesPaneVisible;
        IsScriptListPaneVisible = Settings.IsScriptListPaneVisible;
        IsToolboxPaneVisible = Settings.IsToolboxPaneVisible;
        IsPropertiesPaneVisible = Settings.IsPropertiesPaneVisible;
        IsOutputPaneVisible = Settings.IsOutputPaneVisible;
        ToolboxPaneWidth = IsToolboxPaneVisible ? _lastToolboxPaneWidth : new GridLength(0);
        PropertiesPaneWidth = IsPropertiesPaneVisible ? _lastPropertiesPaneWidth : new GridLength(0);
        OutputPaneHeight = IsOutputPaneVisible ? _lastOutputPaneHeight : new GridLength(0);
        StepsPaneHeight = IsToolboxPaneVisible ? _lastStepsPaneHeight : new GridLength(0);
        ProjectFilesPaneWidth = IsProjectFilesPaneVisible ? _lastProjectFilesPaneWidth : new GridLength(0);
        ScriptListPaneWidth = IsScriptListPaneVisible ? _lastScriptListPaneWidth : new GridLength(0);
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

    private void ToggleProjectFilesPane()
    {
        if (IsProjectFilesPaneVisible)
        {
            if (ProjectFilesPaneWidth.Value > 0) _lastProjectFilesPaneWidth = ProjectFilesPaneWidth;
            ProjectFilesPaneWidth = new GridLength(0);
            IsProjectFilesPaneVisible = false;
        }
        else
        {
            ProjectFilesPaneWidth = _lastProjectFilesPaneWidth;
            IsProjectFilesPaneVisible = true;
        }
        CapturePaneLayout(); PersistSettings();
    }

    private void ToggleScriptListPane()
    {
        if (IsScriptListPaneVisible)
        {
            if (ScriptListPaneWidth.Value > 0) _lastScriptListPaneWidth = ScriptListPaneWidth;
            ScriptListPaneWidth = new GridLength(0);
            IsScriptListPaneVisible = false;
        }
        else
        {
            ScriptListPaneWidth = _lastScriptListPaneWidth;
            IsScriptListPaneVisible = true;
        }
        CapturePaneLayout(); PersistSettings();
    }

    private void ToggleToolboxPane()
    {
        if (IsToolboxPaneVisible)
        {
            if (StepsPaneHeight.Value > 0) _lastStepsPaneHeight = StepsPaneHeight;
            StepsPaneHeight = new GridLength(0);
            IsToolboxPaneVisible = false;
        }
        else
        {
            StepsPaneHeight = _lastStepsPaneHeight;
            IsToolboxPaneVisible = true;
        }
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

    private void ApplyLoadedProject(AutomationProject project, string path, bool rememberPath)
    {
        _project = project; _activeScriptId = string.Empty; EnsureScriptModel(_project);
        _currentPath = rememberPath ? path : null; _dirty = false; Raise(nameof(CurrentFileName)); Raise(nameof(CurrentPathText));
        ProjectName = project.Project.Name; SelectedNode = null;
        if (rememberPath) AddRecent(path);
    }

    private string? ResolveSavePath(bool saveAs) => saveAs || _currentPath is null ? _dialogs.SaveProject(_currentPath) : _currentPath;

    private void ApplySavedProject(AutomationProject project, string path)
    {
        _project = project; EnsureScriptModel(_project);
        _currentPath = path; _dirty = false; Raise(nameof(CurrentFileName)); Raise(nameof(CurrentPathText)); ProjectName = project.Project.Name; AddRecent(path);
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
        await _editor.SendAsync("set_editor_readonly", new { readOnly = true });
        _executionTimer.Restart();
        await _runner.RunAsync(project, CancellationToken.None);
    }

    private async Task FinishRunAsync()
    {
        _executionTimer.Stop(); ElapsedText = $"{_executionTimer.ElapsedMilliseconds} ms";
        await _editor.SendAsync("set_editor_readonly", new { readOnly = false });
        RaiseCommandStates();
    }

    private void UpdateRecentProjects(string path)
    {
        Settings.RecentProjects.Remove(path); Settings.RecentProjects.Insert(0, path);
        while (Settings.RecentProjects.Count > 10) Settings.RecentProjects.RemoveAt(10);
    }

    private void ReportOperationError(Exception exception, string operation)
    {
        _logger.LogError(exception, "{Operation}", operation);
        _dialogs.ShowError(exception.Message);
    }

    private void ApplyTheme()
    {
        var light = string.Equals(Settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
        Application.Current.Resources["AppBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#F1F5F9" : "#0B1120"));
        Application.Current.Resources["PanelBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#E2E8F0" : "#111827"));
    }
}
