using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using GameFlowStudio.Contracts.Nodes;

namespace GameFlowStudio.Desktop.ViewModels;

public sealed class NodeParameterItem(string name, string value, JsonValueKind kind, Action? changed = null) : ObservableObject
{
    private static readonly Dictionary<string, string> ParameterDescriptions = new(StringComparer.Ordinal)
    {
        ["x"] = "横坐标",
        ["y"] = "纵坐标",
        ["startX"] = "起始横坐标",
        ["startY"] = "起始纵坐标",
        ["endX"] = "结束横坐标",
        ["endY"] = "结束纵坐标",
        ["coordinateMode"] = "坐标基准：跟随脚本、强制绝对或强制相对",
        ["imageSearchMode"] = "图片匹配范围：固定坐标或当前屏幕/窗口",
        ["button"] = "鼠标按键",
        ["clickCount"] = "点击次数",
        ["durationMs"] = "持续时间，毫秒",
        ["intervalMs"] = "间隔时间，毫秒",
        ["key"] = "按键",
        ["repeatCount"] = "重复次数",
        ["text"] = "输入文本",
        ["titlePattern"] = "窗口标题正则",
        ["outputVariable"] = "句柄输出变量",
        ["randomMinMs"] = "随机最短时间，毫秒",
        ["randomMaxMs"] = "随机最长时间，毫秒",
        ["message"] = "日志消息",
        ["level"] = "日志级别",
        ["path"] = "程序路径",
        ["arguments"] = "启动参数",
        ["width"] = "宽度",
        ["height"] = "高度",
        ["targetColor"] = "目标颜色（#RRGGBB）",
        ["targetText"] = "匹配文本",
        ["textSearchMode"] = "文本匹配范围：固定坐标或当前屏幕/窗口",
        ["ocrEngine"] = "文本识别引擎",
        ["tolerance"] = "检测容差",
        ["minMatchRatio"] = "最低匹配比例",
        ["templatePath"] = "图片模板路径",
        ["imageMatchEngine"] = "图像匹配引擎",
        ["matchThreshold"] = "图片匹配阈值",
        ["scriptId"] = "目标脚本 ID",
        ["actionId"] = "目标动作 ID",
        ["offsetY"] = "窗口向下偏移距离（像素）",
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
            if (!Set(ref _value, value)) return;
            Raise(nameof(BooleanValue));
            changed?.Invoke();
        }
    }
    public bool BooleanValue
    {
        get => bool.TryParse(Value, out var value) && value;
        set => Value = value ? "true" : "false";
    }
}

public sealed class SelectedNodeViewModel : ObservableObject
{
    private static readonly IReadOnlySet<string> InternalParameterNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "recordingSessionId",
            "recordingBoundary",
            "singleWindow",
            "activateWindow"
        };
    private string _displayName = string.Empty;
    private Action? _changed;
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value)) _changed?.Invoke();
        }
    }
    public ObservableCollection<NodeParameterItem> Parameters { get; } = [];
    public bool IsDetectorCondition => Type is NodeTypes.ColorCondition or NodeTypes.ImageCondition or NodeTypes.OcrCondition;
    public bool SupportsCoordinateMode => Type is NodeTypes.ColorCondition or NodeTypes.ImageCondition or NodeTypes.OcrCondition
        or NodeTypes.MouseClick or NodeTypes.MouseMove or NodeTypes.MouseDrag or NodeTypes.MouseScroll;
    public IEnumerable<NodeParameterItem> NumericParameters => VisibleParameters.Where(parameter => parameter.Kind == JsonValueKind.Number
        && parameter.Name is not ("minMatchRatio" or "matchThreshold")
        && (!IsCurrentDetectorSearch || parameter.Name is not ("x" or "y" or "width" or "height")));
    public IEnumerable<NodeParameterItem> TextParameters => VisibleParameters.Where(parameter => parameter.Kind == JsonValueKind.String && !IsChoiceParameter(parameter.Name) && parameter.Name != "coordinateMode");
    public IEnumerable<NodeParameterItem> ChoiceParameters => VisibleParameters.Where(parameter => parameter.Kind == JsonValueKind.String && IsChoiceParameter(parameter.Name) && parameter.Name != "coordinateMode");
    public IEnumerable<NodeParameterItem> CoordinateParameters => VisibleParameters.Where(parameter => parameter.Name == "coordinateMode");
    public NodeParameterItem? ImageSearchModeParameter => Parameters.FirstOrDefault(parameter => parameter.Name == "imageSearchMode");
    public NodeParameterItem? TextSearchModeParameter => Parameters.FirstOrDefault(parameter => parameter.Name == "textSearchMode");
    public NodeParameterItem? ImageMatchEngineParameter => Parameters.FirstOrDefault(parameter => parameter.Name == "imageMatchEngine");
    public NodeParameterItem? OcrEngineParameter => Parameters.FirstOrDefault(parameter => parameter.Name == "ocrEngine");
    public IEnumerable<NodeParameterItem> BooleanParameters => VisibleParameters.Where(parameter => parameter.Kind is JsonValueKind.True or JsonValueKind.False);
    public IEnumerable<NodeParameterItem> OtherParameters => VisibleParameters.Where(parameter => parameter.Kind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False));
    public bool HasNumericParameters => NumericParameters.Any();
    public bool HasTextParameters => TextParameters.Any();
    public bool HasChoiceParameters => ChoiceParameters.Any();
    public bool HasCoordinateParameters => SupportsCoordinateMode && !IsCurrentDetectorSearch;
    public bool HasImageSearchModeParameter => ImageSearchModeParameter is not null;
    public bool HasTextSearchModeParameter => TextSearchModeParameter is not null;
    public bool HasImageMatchEngineParameter => ImageMatchEngineParameter is not null;
    public bool HasOcrEngineParameter => OcrEngineParameter is not null;
    public bool HasBooleanParameters => BooleanParameters.Any();
    public bool HasOtherParameters => OtherParameters.Any();
    public NodeParameterItem? ConfidenceParameter => Type switch
    {
        NodeTypes.ColorCondition => Parameters.FirstOrDefault(parameter => parameter.Name == "minMatchRatio"),
        NodeTypes.ImageCondition => Parameters.FirstOrDefault(parameter => parameter.Name == "matchThreshold"),
        NodeTypes.OcrCondition => Parameters.FirstOrDefault(parameter => parameter.Name == "matchThreshold"),
        _ => null
    };
    public bool HasConfidenceParameter => ConfidenceParameter is not null;
    public string ConfidenceLabel => Type switch
    {
        NodeTypes.ColorCondition => "颜色匹配置信阈值",
        NodeTypes.ImageCondition => "图像匹配置信阈值",
        NodeTypes.OcrCondition => "文本匹配阈值",
        _ => "匹配置信阈值"
    };
    public string ConfidenceHint => Type == NodeTypes.ColorCondition
        ? "区域内达到此比例的像素匹配目标颜色时判定为命中。"
        : Type == NodeTypes.ImageCondition
            ? "模板匹配得分达到此比例时判定为命中。"
            : "识别文本与目标文本的相似度达到此比例时判定为命中。";
    public string LogicLabel => Type switch
    {
        NodeTypes.ColorCondition => "色判断",
        NodeTypes.ImageCondition => "图判断",
        NodeTypes.OcrCondition => "文本判断",
        _ => "逻辑判断"
    };
    public bool CanPickCoordinate => !IsCurrentDetectorSearch
        && Type is NodeTypes.ColorCondition or NodeTypes.ImageCondition or NodeTypes.OcrCondition or NodeTypes.MouseClick or NodeTypes.MouseMove or NodeTypes.MouseDrag or NodeTypes.MouseScroll;
    public bool CanPickProgramPath => Type == NodeTypes.RunProgram && Parameters.Any(parameter => parameter.Name == "path");
    public string CoordinatePickButtonLabel => Type switch
    {
        NodeTypes.ColorCondition => "拾取色值点",
        NodeTypes.ImageCondition => "取图",
        NodeTypes.OcrCondition => "拾取区域",
        NodeTypes.MouseDrag => "拾取起止点",
        _ => "拾取坐标"
    };
    public string CoordinatePickButtonToolTip => Type switch
    {
        NodeTypes.ColorCondition => "选择拾取模式后点击屏幕，同时记录坐标和颜色值。",
        NodeTypes.ImageCondition => "选择拾取模式后拖动框选矩形截图，同时记录坐标和尺寸。",
        NodeTypes.OcrCondition => "选择拾取模式后拖动框选文本识别区域，同时记录坐标和尺寸。",
        _ => "先选择基于屏幕或基于窗口的拾取模式。"
    };
    public string GetParameterValue(string name, string fallback) =>
        Parameters.FirstOrDefault(parameter => parameter.Name == name)?.Value ?? fallback;

    public void SetChangedHandler(Action changed) => _changed = changed;
    public void RefreshParameterViews()
    {
        Raise(nameof(NumericParameters));
        Raise(nameof(HasNumericParameters));
        Raise(nameof(HasCoordinateParameters));
        Raise(nameof(CanPickCoordinate));
    }
    public int GetParameterInt(string name, int fallback) =>
        int.TryParse(GetParameterValue(name, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private bool IsCurrentDetectorSearch =>
        (Type == NodeTypes.ImageCondition && string.Equals(ImageSearchModeParameter?.Value, "current", StringComparison.OrdinalIgnoreCase))
        || (Type == NodeTypes.OcrCondition && string.Equals(TextSearchModeParameter?.Value, "current", StringComparison.OrdinalIgnoreCase));

    private IEnumerable<NodeParameterItem> VisibleParameters =>
        Parameters.Where(parameter => !InternalParameterNames.Contains(parameter.Name));

    private static bool IsChoiceParameter(string name) => name is "coordinateMode"
        or "button" or "level" or "imageSearchMode" or "textSearchMode" or "imageMatchEngine" or "ocrEngine";
}
