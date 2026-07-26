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
using GameFlowStudio.Contracts.Services;
using GameFlowStudio.Contracts.Workflows;
using GameFlowStudio.Contracts.States;
using GameFlowStudio.Desktop.Services;
using GameFlowStudio.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.ViewModels;

public sealed record ToolboxItem(string Type, string Label, string Category, bool IsEnabled = true)
{
    public string? ToolTip => IsEnabled ? null : "识图点击暂未实现";
}

public sealed class ToolboxGroup(string name, IReadOnlyList<ToolboxItem> items) : ObservableObject
{
    private bool _isExpanded = true;

    public string Name { get; } = name;
    public IReadOnlyList<ToolboxItem> Items { get; } = items;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
}

public sealed record ProjectFileItem(string Name, string Path);

public sealed class ScriptListItem(string id, string name, string description) : ObservableObject
{
    private bool _isActive;
    private string _name = name;

    public string Id { get; } = id;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Description { get; } = description;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}

public sealed class NodeParameterItem(string name, string value, JsonValueKind kind) : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> ParameterDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["x"] = "横坐标",
        ["y"] = "纵坐标",
        ["startX"] = "起始横坐标",
        ["startY"] = "起始纵坐标",
        ["endX"] = "结束横坐标",
        ["endY"] = "结束纵坐标",
        ["coordinateMode"] = "坐标模式（screen 或 windowRelative）",
        ["button"] = "鼠标按键",
        ["clickCount"] = "点击次数",
        ["durationMs"] = "持续时间，毫秒",
        ["intervalMs"] = "间隔时间，毫秒",
        ["key"] = "按键",
        ["repeatCount"] = "重复次数",
        ["text"] = "输入文本",
        ["titlePattern"] = "窗口标题正则",
        ["outputVariable"] = "句柄输出变量",
        ["windowHandleVariable"] = "窗口句柄变量",
        ["windowHandleIndex"] = "窗口句柄索引",
        ["randomMinMs"] = "随机最短时间，毫秒",
        ["randomMaxMs"] = "随机最长时间，毫秒",
        ["message"] = "日志消息",
        ["level"] = "日志级别",
        ["path"] = "程序路径",
        ["arguments"] = "启动参数",
        ["workingDirectory"] = "工作目录",
        ["waitForExit"] = "等待程序退出",
        ["width"] = "宽度",
        ["height"] = "高度",
        ["targetColor"] = "目标颜色（#RRGGBB）",
        ["tolerance"] = "检测容差",
        ["minMatchRatio"] = "最低匹配比例",
        ["templatePath"] = "图片模板路径",
        ["matchThreshold"] = "图片匹配阈值",
        ["columns"] = "列数",
        ["gapX"] = "横向间距",
        ["gapY"] = "纵向间距",
        ["delta"] = "滚动量（正数向上，负数向下）",
    };

    private string _value = value;
    public string Name { get; } = name;
    public string Description => ParameterDescriptions.TryGetValue(Name, out var description) ? description : "参数";
    public string DisplayName => $"{Name}（{Description}）";
    public JsonValueKind Kind { get; } = kind;
    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) Raise(nameof(BooleanValue));
        }
    }
    public bool BooleanValue
    {
        get => bool.TryParse(Value, out var value) && value;
        set => Value = value ? "true" : "false";
    }
}

public sealed class DetectorEditorItem : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private DetectorType _type;
    private bool _enabled;
    private string _parametersJson;
    private string _parametersError = string.Empty;
    public DetectorDefinition Definition { get; }
    public string Id => Definition.Id;
    public bool HasParametersError => !string.IsNullOrEmpty(_parametersError);
    public string ParametersError => _parametersError;
    public string Name { get => _name; set { if (Set(ref _name, value)) { Definition.Name = value; _changed(); } } }
    public DetectorType Type { get => _type; set { if (Set(ref _type, value)) { Definition.Type = value; _changed(); } } }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Definition.Enabled = value; _changed(); } } }
    public string ParametersJson
    {
        get => _parametersJson;
        set
        {
            if (!Set(ref _parametersJson, value)) return;
            try
            {
                var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value, ProjectJson.Options);
                if (parameters is null) throw new JsonException();
                Definition.Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
                SetParametersError(string.Empty);
                _changed();
            }
            catch (JsonException) { SetParametersError("检测器参数必须是有效的 JSON 对象。"); }
        }
    }

    public DetectorEditorItem(DetectorDefinition definition, Action changed)
    {
        Definition = definition; _changed = changed; _name = definition.Name; _type = definition.Type; _enabled = definition.Enabled;
        _parametersJson = JsonSerializer.Serialize(definition.Parameters, ProjectJson.Options);
    }

    private void SetParametersError(string error)
    {
        if (Set(ref _parametersError, error))
        {
            Raise(nameof(HasParametersError));
            Raise(nameof(ParametersError));
        }
    }
}

public sealed class DetectorRuleEditorItem : ObservableObject
{
    private readonly Action _changed;
    private string _detectorId;
    private int _state;

    public string DetectorId { get => _detectorId; set { if (Set(ref _detectorId, value)) _changed(); } }
    public int State
    {
        get => _state;
        set
        {
            var normalized = value == 0 ? 0 : 1;
            if (Set(ref _state, normalized))
            {
                Raise(nameof(StateLabel));
                _changed();
            }
        }
    }
    public string StateLabel => State == 1 ? "1 · 命中" : "0 · 未命中";

    public DetectorRuleEditorItem(StateDetectorRule rule, Action changed)
    {
        _changed = changed; _detectorId = rule.DetectorId; _state = rule.State is 0 ? 0 : 1;
    }

    public StateDetectorRule ToRule() => new() { DetectorId = DetectorId, State = State };
}

public sealed class SelectedNodeViewModel : ObservableObject
{
    private string _displayName = string.Empty;
    private bool _enabled = true;
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public ObservableCollection<NodeParameterItem> Parameters { get; } = [];
    public ObservableCollection<DetectorEditorItem> Detectors { get; } = [];
    public ObservableCollection<DetectorRuleEditorItem> StateRules { get; } = [];
    public bool IsStateCondition => Type == NodeTypes.StateCondition;
    public bool IsSingleStateCondition => Type is NodeTypes.ColorCondition or NodeTypes.ImageCondition;
    public IEnumerable<NodeParameterItem> NumericParameters => Parameters.Where(parameter => parameter.Kind == JsonValueKind.Number && parameter.Name != "state");
    public IEnumerable<NodeParameterItem> TextParameters => Parameters.Where(parameter => parameter.Kind == JsonValueKind.String && !IsChoiceParameter(parameter.Name));
    public IEnumerable<NodeParameterItem> ChoiceParameters => Parameters.Where(parameter => parameter.Kind == JsonValueKind.String && IsChoiceParameter(parameter.Name));
    public IEnumerable<NodeParameterItem> BooleanParameters => Parameters.Where(parameter => parameter.Kind is JsonValueKind.True or JsonValueKind.False);
    public IEnumerable<NodeParameterItem> OtherParameters => Parameters.Where(parameter => parameter.Kind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False) && parameter.Name != "state");
    public IEnumerable<NodeParameterItem> StateParameters => Parameters.Where(parameter => parameter.Name == "state");
    public bool HasNumericParameters => NumericParameters.Any();
    public bool HasTextParameters => TextParameters.Any();
    public bool HasChoiceParameters => ChoiceParameters.Any();
    public bool HasBooleanParameters => BooleanParameters.Any();
    public bool HasOtherParameters => OtherParameters.Any();
    public string LogicLabel => Type switch
    {
        NodeTypes.ColorCondition => "色判断",
        NodeTypes.ImageCondition => "图判断",
        _ => "联合判断"
    };
    public string StateRuleSummary => StateRules.Count switch
    {
        0 => "未配置检测器",
        1 => "单一判断",
        _ => $"{StateRules.Count} 个检测器 · 联合满足"
    };
    public bool CanPickCoordinate => Type is NodeTypes.MouseClick or NodeTypes.MouseMove;
    public bool UsesWindowRelativeCoordinates => Type == NodeTypes.MouseClick &&
        string.Equals(GetParameterValue("coordinateMode", "screen"), "windowRelative", StringComparison.OrdinalIgnoreCase);

    public string GetParameterValue(string name, string fallback) =>
        Parameters.FirstOrDefault(parameter => parameter.Name == name)?.Value ?? fallback;

    public void RefreshStateSummary() => Raise(nameof(StateRuleSummary));

    public int GetParameterInt(string name, int fallback) =>
        int.TryParse(GetParameterValue(name, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool IsChoiceParameter(string name) => name is "coordinateMode" or "button" or "level";
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IEditorBridge _editor;
    private readonly IWorkflowRunner _runner;
    private readonly IWorkflowValidator _validator;
    private readonly IProjectStorageService _storage;
    private readonly ISettingsStorageService _settingsStorage;
    private readonly IFileDialogService _dialogs;
    private readonly ICoordinatePickerService _coordinatePicker;
    private readonly IWindowSelectionService _windowSelection;
    private readonly IRelativeCoordinatePickerService _relativeCoordinatePicker;
    private readonly ILogger<MainViewModel> _logger;
    private AutomationProject _project = ProjectFactory.CreateNew();
    private string _activeScriptId = string.Empty;
    private string? _currentPath;
    private string _projectName = "未命名项目";
    private string _stateText = "未运行";
    private string _currentNodeText = "—";
    private string _elapsedText = "—";
    private bool _dirty;
    private SelectedNodeViewModel? _selectedNode;
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
    private string _scriptWindowOutputVariable = "windowHandles";
    private string _activeScriptName = string.Empty;
    private int _scriptDefaultNodeDelayMs;

    public MainViewModel(IEditorBridge editor, IWorkflowRunner runner, IWorkflowValidator validator, IProjectStorageService storage,
        ISettingsStorageService settingsStorage, IFileDialogService dialogs, ICoordinatePickerService coordinatePicker,
        IWindowSelectionService windowSelection, IRelativeCoordinatePickerService relativeCoordinatePicker,
        GameFlowLogProvider logProvider, ILogger<MainViewModel> logger)
    {
        _editor = editor; _runner = runner; _validator = validator; _storage = storage; _settingsStorage = settingsStorage;
        _dialogs = dialogs; _coordinatePicker = coordinatePicker; _windowSelection = windowSelection; _relativeCoordinatePicker = relativeCoordinatePicker; _logger = logger;
        ToolboxGroups = NodeCatalog.Groups;
        EnsureScriptModel(_project);
        InitializeCommands();
        SubscribeToEvents(logProvider);
        RestoreLogEntries(logProvider);
    }

    public ObservableCollection<DisplayLogEntry> Logs { get; } = [];
    public ObservableCollection<ProjectFileItem> RecentProjectFiles { get; } = [];
    public ObservableCollection<ScriptListItem> Scripts { get; } = [];
    public IReadOnlyList<DetectorType> DetectorTypes { get; } = Enum.GetValues<DetectorType>();
    public IReadOnlyList<ToolboxGroup> ToolboxGroups { get; }
    public AppSettings Settings { get; private set; } = new();
    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }
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
                Raise(nameof(HasStateConditionSelection));
                Raise(nameof(HasSingleStateConditionSelection));
                Raise(nameof(IsRegularNodeSelection));
                RaiseCommandStates();
            }
        }
    }
    public bool HasSelectedNode => SelectedNode is not null;
    public bool HasStateConditionSelection => SelectedNode?.IsStateCondition == true;
    public bool HasSingleStateConditionSelection => SelectedNode?.IsSingleStateCondition == true;
    public bool IsRegularNodeSelection => SelectedNode is { IsStateCondition: false, IsSingleStateCondition: false };
    public bool HasPropertySelection => HasSelectedNode || IsScriptPropertiesVisible;
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
    public string ScriptWindowOutputVariable
    {
        get => _scriptWindowOutputVariable;
        set { if (Set(ref _scriptWindowOutputVariable, value)) { ActiveScript.WindowScope.OutputVariable = value; MarkScriptSettingsChanged(); } }
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
    public ICommand PauseCommand { get; private set; } = null!; public ICommand ResumeCommand { get; private set; } = null!; public ICommand StopCommand { get; private set; } = null!;
    public ICommand AddNodeCommand { get; private set; } = null!; public ICommand ApplyNodeCommand { get; private set; } = null!; public ICommand PickCoordinateCommand { get; private set; } = null!;
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
    public ICommand AddStateDetectorCommand { get; private set; } = null!;
    public ICommand RemoveStateDetectorCommand { get; private set; } = null!;
    public ICommand AddStateRuleCommand { get; private set; } = null!;
    public ICommand RemoveStateRuleCommand { get; private set; } = null!;

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

    public bool CanClose()
    {
        if (_dirty && !_dialogs.ConfirmDiscardChanges()) return false;
        StopActiveWorkflow();
        CaptureWindowSettings();
        CapturePaneLayout();
        PersistSettings();
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
        var selected = new SelectedNodeViewModel { Id = payload.GetProperty("nodeId").GetString()!, Type = payload.GetProperty("nodeType").GetString()!, DisplayName = data.GetProperty("displayName").GetString() ?? string.Empty, Enabled = data.GetProperty("enabled").GetBoolean() };
        if (data.TryGetProperty("parameters", out var parameters))
        {
            foreach (var property in parameters.EnumerateObject()) selected.Parameters.Add(new(property.Name, DisplayValue(property.Value), property.Value.ValueKind));
            if (selected.IsStateCondition)
            {
                if (parameters.TryGetProperty("detectors", out var detectorsElement))
                {
                    var detectors = detectorsElement.Deserialize<List<DetectorDefinition>>(ProjectJson.Options) ?? [];
                    foreach (var detector in detectors) selected.Detectors.Add(new DetectorEditorItem(detector, selected.RefreshStateSummary));
                }

                if (!parameters.TryGetProperty("detectorRules", out var rulesElement))
                {
                    SelectedNode = selected;
                    return;
                }

                var rules = rulesElement.Deserialize<List<StateDetectorRule>>(ProjectJson.Options) ?? [];
                foreach (var rule in rules) selected.StateRules.Add(new DetectorRuleEditorItem(rule, selected.RefreshStateSummary));
            }
        }
        SelectedNode = selected;
    }

    private async Task ApplySelectedNodeAsync()
    {
        if (SelectedNode is null) return;
        if (!TryParseNodeParameters(SelectedNode.Parameters, out var parameters, out var error))
        {
            _dialogs.ShowError(error);
            return;
        }

        if (SelectedNode.IsStateCondition)
        {
            if (!ValidateStateDetectors(SelectedNode.Detectors, out error))
            {
                _dialogs.ShowError(error);
                return;
            }

            if (!ValidateStateRules(SelectedNode.StateRules, out error))
            {
                _dialogs.ShowError(error);
                return;
            }

            parameters["detectors"] = ProjectJson.Element(SelectedNode.Detectors.Select(detector => detector.Definition).ToList());
            parameters["detectorRules"] = ProjectJson.Element(SelectedNode.StateRules.Select(rule => rule.ToRule()).ToList());
        }
        await _editor.SendAsync("update_node_data", new { nodeId = SelectedNode.Id, data = new { displayName = SelectedNode.DisplayName, enabled = SelectedNode.Enabled, parameters } });
    }

    private bool ValidateStateRules(IReadOnlyList<DetectorRuleEditorItem> rules, out string error)
    {
        if (rules.Count == 0)
        {
            error = "联合判断至少需要一个检测器规则。";
            return false;
        }

        if (rules.Any(rule => SelectedNode!.Detectors.All(detector => detector.Id != rule.DetectorId)))
        {
            error = "联合判断包含不存在的检测器，请重新选择。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateStateDetectors(IReadOnlyList<DetectorEditorItem> detectors, out string error)
    {
        var invalid = detectors.FirstOrDefault(detector => detector.HasParametersError);
        if (invalid is not null)
        {
            error = $"检测器“{invalid.Name}”的参数 JSON 无效，请修正后再应用。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task PickCoordinateAsync()
    {
        if (SelectedNode is null) return;
        var point = await PickCoordinateForNodeAsync(); if (point is null) return;
        SetParameter("x", point.Value.X); SetParameter("y", point.Value.Y); await ApplySelectedNodeAsync();
    }

    private async Task<(int X, int Y)?> PickCoordinateForNodeAsync()
    {
        if (SelectedNode is null || !SelectedNode.UsesWindowRelativeCoordinates)
            return await _coordinatePicker.PickAsync();

        var window = _windowSelection.SelectWindow();
        return window is null ? null : await _relativeCoordinatePicker.PickAsync(window);
    }

    private void SetParameter(string name, int value)
    {
        var item = SelectedNode!.Parameters.FirstOrDefault(p => p.Name == name);
        if (item is null) SelectedNode.Parameters.Add(new(name, value.ToString(CultureInfo.InvariantCulture), JsonValueKind.Number)); else item.Value = value.ToString(CultureInfo.InvariantCulture);
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

    private async Task AddNodeAsync(ToolboxItem? item) { if (item is not null) await _editor.SendAsync("add_node", new { nodeType = item.Type }); }

    private async Task NewScriptAsync()
    {
        await ExportAsync();
        var name = CreateScriptName();
        var script = ProjectFactory.CreateScript(name);
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
        if (!_dialogs.EditSettings(Settings)) return;
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
                Id = $"{project.Project.Id}-main",
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
        _project.Variables = script.Variables;
        _project.Nodes = script.Nodes;
        _project.Edges = script.Edges;
        _project.WindowScope = script.WindowScope;
        _project.DefaultNodeDelayMs = script.DefaultNodeDelayMs;
    }

    private void RefreshScriptSettings()
    {
        var script = ActiveScript;
        script.WindowScope ??= new();
        _activeScriptName = script.Name;
        _scriptWindowScopeEnabled = script.WindowScope.Enabled;
        _scriptWindowTitlePattern = script.WindowScope.TitlePattern;
        _scriptWindowOutputVariable = script.WindowScope.OutputVariable;
        _scriptDefaultNodeDelayMs = Math.Clamp(script.DefaultNodeDelayMs, 0, 60000);
        Raise(nameof(ActiveScriptName));
        Raise(nameof(UseWholeScreen));
        Raise(nameof(ScriptWindowScopeEnabled));
        Raise(nameof(ScriptWindowTitlePattern));
        Raise(nameof(ScriptWindowOutputVariable));
        Raise(nameof(ScriptDefaultNodeDelayMs));
    }

    private void MarkScriptSettingsChanged()
    {
        if (_project.Scripts.Count == 0) return;
        SyncRuntimeScriptView(ActiveScript);
        _dirty = true;
    }

    private void AddStateDetector()
    {
        if (SelectedNode is not { IsStateCondition: true }) return;
        var detector = CreateColorDetector($"检测器 {SelectedNode.Detectors.Count + 1}");
        SelectedNode.Detectors.Add(new DetectorEditorItem(detector, SelectedNode.RefreshStateSummary));
        SelectedNode.RefreshStateSummary();
    }

    private static DetectorDefinition CreateColorDetector(string name)
    {
        var detector = new DetectorDefinition { Name = name, Type = DetectorType.Color };
        detector.Parameters["x"] = ProjectJson.Element(0);
        detector.Parameters["y"] = ProjectJson.Element(0);
        detector.Parameters["width"] = ProjectJson.Element(1);
        detector.Parameters["height"] = ProjectJson.Element(1);
        detector.Parameters["targetColor"] = ProjectJson.Element("#FFFFFF");
        detector.Parameters["tolerance"] = ProjectJson.Element(10);
        return detector;
    }

    private void RemoveStateDetector(DetectorEditorItem? item)
    {
        if (SelectedNode is not { IsStateCondition: true } || item is null) return;
        if (SelectedNode.StateRules.Any(rule => rule.DetectorId == item.Id))
        {
            _dialogs.ShowError("检测器仍被联合判断规则使用，请先移除对应规则。");
            return;
        }

        SelectedNode.Detectors.Remove(item);
        SelectedNode.RefreshStateSummary();
    }

    private void AddStateRule()
    {
        if (SelectedNode is not { IsStateCondition: true }) return;
        var detector = SelectedNode.Detectors.FirstOrDefault();
        if (detector is null)
        {
            _dialogs.ShowError("请先添加检测器，再添加联合判断规则。");
            return;
        }

        SelectedNode.StateRules.Add(new DetectorRuleEditorItem(new StateDetectorRule { DetectorId = detector.Id, State = 1 }, SelectedNode.RefreshStateSummary));
        SelectedNode.RefreshStateSummary();
    }

    private void RemoveStateRule(DetectorRuleEditorItem? rule)
    {
        if (SelectedNode is not { IsStateCondition: true } || rule is null) return;
        SelectedNode.StateRules.Remove(rule);
        SelectedNode.RefreshStateSummary();
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

    private void OnNodeStarted(NodeExecutionEventArgs e) => Application.Current.Dispatcher.Invoke(() => { CurrentNodeText = e.Node.Data.DisplayName; _ = _editor.SendAsync("highlight_node", new { nodeId = e.Node.Id }); });
    private void RaiseCommandStates() { foreach (var command in new[] { RunCommand, PauseCommand, ResumeCommand, StopCommand, ApplyNodeCommand, PickCoordinateCommand }) if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged(); }
    private static string StateLabel(WorkflowExecutionState state) => state switch { WorkflowExecutionState.Running => "正在运行", WorkflowExecutionState.Paused => "已暂停", WorkflowExecutionState.Stopping => "正在停止", WorkflowExecutionState.Completed => "已完成", WorkflowExecutionState.Failed => "执行失败", _ => "未运行" };
    private static string DisplayValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static bool TryParseNodeParameters(IReadOnlyList<NodeParameterItem> items, out Dictionary<string, JsonElement> parameters, out string error)
    {
        parameters = new(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!TryParseValue(item, out var value))
            {
                error = $"参数“{item.Name}”格式无效，请检查后再应用。";
                return false;
            }

            parameters[item.Name] = value;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseValue(NodeParameterItem item, out JsonElement value)
    {
        switch (item.Kind)
        {
            case JsonValueKind.Number when IsDecimalParameter(item.Name) && double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue):
                value = ProjectJson.Element(decimalValue);
                return true;
            case JsonValueKind.Number when int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue):
                value = ProjectJson.Element(integerValue);
                return true;
            case JsonValueKind.True or JsonValueKind.False when bool.TryParse(item.Value, out var booleanValue):
                value = ProjectJson.Element(booleanValue);
                return true;
            case JsonValueKind.Array or JsonValueKind.Object:
                try
                {
                    value = ParseJson(item.Value);
                    return true;
                }
                catch (JsonException)
                {
                    break;
                }
            default:
                value = ProjectJson.Element(item.Value);
                return true;
        }

        value = default;
        return false;
    }

    private static bool IsDecimalParameter(string name) => name is "minMatchRatio" or "matchThreshold";

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private void InitializeCommands()
    {
        NewCommand = new AsyncRelayCommand(_ => NewAsync()); OpenCommand = new AsyncRelayCommand(_ => OpenAsync()); OpenRecentProjectCommand = new AsyncRelayCommand(item => OpenRecentAsync(item as ProjectFileItem));
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(false)); SaveAsCommand = new AsyncRelayCommand(_ => SaveAsync(true));
        OpenSampleCommand = new AsyncRelayCommand(_ => OpenSampleAsync()); RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => !IsWorkflowActive());
        PauseCommand = new AsyncRelayCommand(_ => _runner.PauseAsync()); ResumeCommand = new AsyncRelayCommand(_ => _runner.ResumeAsync()); StopCommand = new AsyncRelayCommand(_ => _runner.StopAsync());
        AddNodeCommand = new AsyncRelayCommand(item => AddNodeAsync(item as ToolboxItem)); ApplyNodeCommand = new AsyncRelayCommand(_ => ApplySelectedNodeAsync(), _ => SelectedNode is not null);
        PickCoordinateCommand = new AsyncRelayCommand(_ => PickCoordinateAsync(), _ => SelectedNode?.CanPickCoordinate == true);
        NewScriptCommand = new AsyncRelayCommand(_ => NewScriptAsync());
        SelectScriptCommand = new AsyncRelayCommand(item => SelectScriptAsync(item as ScriptListItem));
        OpenScriptPropertiesCommand = new AsyncRelayCommand(item => OpenScriptPropertiesAsync(item as ScriptListItem));
        DeleteScriptCommand = new AsyncRelayCommand(item => DeleteScriptAsync(item as ScriptListItem));
        AddStateDetectorCommand = new RelayCommand(_ => AddStateDetector());
        RemoveStateDetectorCommand = new RelayCommand(item => RemoveStateDetector(item as DetectorEditorItem));
        AddStateRuleCommand = new RelayCommand(_ => AddStateRule());
        RemoveStateRuleCommand = new RelayCommand(item => RemoveStateRule(item as DetectorRuleEditorItem));
        SettingsCommand = new AsyncRelayCommand(_ => EditSettingsAsync());
        ToggleProjectFilesPaneCommand = new RelayCommand(_ => ToggleProjectFilesPane());
        ToggleScriptListPaneCommand = new RelayCommand(_ => ToggleScriptListPane());
        ToggleToolboxPaneCommand = new RelayCommand(_ => ToggleToolboxPane());
        TogglePropertiesPaneCommand = new RelayCommand(_ => TogglePropertiesPane());
        ToggleOutputPaneCommand = new RelayCommand(_ => ToggleOutputPane());
    }

    private void SubscribeToEvents(GameFlowLogProvider logProvider)
    {
        _editor.MessageReceived += OnEditorMessage;
        _runner.NodeStarted += (_, args) => OnNodeStarted(args);
        _runner.NodeCompleted += (_, args) => _ = _editor.SendAsync("mark_node_success", new { nodeId = args.Node.Id });
        _runner.NodeFailed += (_, args) => _ = _editor.SendAsync("mark_node_error", new { nodeId = args.Node.Id });
        _runner.StateChanged += OnWorkflowStateChanged;
        logProvider.EntryAdded += OnLogEntryAdded;
    }

    private void RestoreLogEntries(GameFlowLogProvider logProvider)
    {
        foreach (var entry in logProvider.Entries) Logs.Add(entry);
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateChangedEventArgs args) =>
        Application.Current.Dispatcher.Invoke(() => { StateText = StateLabel(args.Current); RaiseCommandStates(); });

    private void OnLogEntryAdded(object? sender, DisplayLogEntry entry) =>
        Application.Current.Dispatcher.Invoke(() => { Logs.Add(entry); while (Logs.Count > 1000) Logs.RemoveAt(0); });

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
        IsProjectFilesPaneVisible = true;
        IsScriptListPaneVisible = true;
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
        if (IsToolboxPaneVisible && ToolboxPaneWidth.Value > 0) _lastToolboxPaneWidth = ToolboxPaneWidth;
        if (IsPropertiesPaneVisible && PropertiesPaneWidth.Value > 0) _lastPropertiesPaneWidth = PropertiesPaneWidth;
        if (IsOutputPaneVisible && OutputPaneHeight.Value > 0) _lastOutputPaneHeight = OutputPaneHeight;
        Settings.ToolboxPaneWidth = _lastToolboxPaneWidth.Value;
        Settings.PropertiesPaneWidth = _lastPropertiesPaneWidth.Value;
        Settings.OutputPaneHeight = _lastOutputPaneHeight.Value;
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

    private void PersistSettings() => _ = _settingsStorage.SaveAsync(Settings, CancellationToken.None);

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
        _logger.LogError(exception, operation);
        _dialogs.ShowError(exception.Message);
    }

    private void ApplyTheme()
    {
        var light = string.Equals(Settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
        Application.Current.Resources["AppBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#F1F5F9" : "#0B1120"));
        Application.Current.Resources["PanelBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#E2E8F0" : "#111827"));
    }
}
