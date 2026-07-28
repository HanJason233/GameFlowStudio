namespace GameFlowStudio.Contracts.Nodes;

/// <summary>内置节点类型的稳定标识。</summary>
public static class NodeTypes
{
    public const string Start = "start";
    public const string WindowScope = "windowScope";
    public const string MouseClick = "mouseClick";
    public const string MouseMove = "mouseMove";
    public const string MouseDrag = "mouseDrag";
    public const string MouseScroll = "mouseScroll";
    public const string KeyPress = "keyPress";
    public const string TextInput = "textInput";
    public const string Wait = "wait";
    public const string ColorCondition = "colorCondition";
    public const string ImageCondition = "imageCondition";
    public const string OcrCondition = "ocrCondition";
    public const string Jump = "jump";
    public const string RunProgram = "runProgram";
    public const string CloseProgram = "closeProgram";
    public const string MinimizeWindow = "minimizeWindow";
    public const string ArrangeWindows = "arrangeWindows";
    public const string SetWindowResolution = "setWindowResolution";
    public const string End = "end";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Start, WindowScope, MouseClick, MouseMove, MouseDrag, MouseScroll, KeyPress, TextInput, Wait,
        ColorCondition, ImageCondition, OcrCondition, Jump, RunProgram, CloseProgram, MinimizeWindow, ArrangeWindows, SetWindowResolution, End
    };

    public static readonly IReadOnlySet<string> Structural = new HashSet<string>(StringComparer.Ordinal)
    {
        Start, ColorCondition, ImageCondition, OcrCondition, End
    };
}
