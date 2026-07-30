using GameFlowStudio.Contracts.Nodes;
using GameFlowStudio.Desktop.ViewModels;

namespace GameFlowStudio.Desktop.Services;

internal static class NodeCatalog
{
    public static IReadOnlyList<ToolboxItem> Items { get; } =
    [
        new(NodeTypes.ColorCondition, "色判断", "逻辑类"),
        new(NodeTypes.ImageCondition, "图判断", "逻辑类"),
        new(NodeTypes.OcrCondition, "文本判断", "逻辑类"),
        new(NodeTypes.Jump, "跳转", "逻辑类"),
        new(NodeTypes.Start, "开始", "基础类"),
        new(NodeTypes.End, "结束", "基础类"),
        new(NodeTypes.Wait, "等待", "基础类"),
        new(NodeTypes.KeyPress, "键盘按键", "键盘"),
        new(NodeTypes.TextInput, "输入文本", "键盘"),
        new(NodeTypes.MouseClick, "点击", "鼠标操作"),
        new(NodeTypes.MouseDrag, "鼠标拖动", "鼠标操作"),
        new(NodeTypes.MouseScroll, "鼠标滑动", "鼠标操作"),
        new(NodeTypes.RunProgram, "启动程序", "窗口与程序"),
        new(NodeTypes.CloseProgram, "关闭程序", "窗口与程序"),
        new(NodeTypes.MinimizeWindow, "最小化窗口", "窗口与程序"),
        new(NodeTypes.ArrangeWindows, "窗口整理", "窗口与程序"),
        new(NodeTypes.SetWindowResolution, "设置窗口分辨率", "窗口与程序"),
    ];

    public static IReadOnlyList<ToolboxGroup> Groups { get; } =
    [
        CreateGroup("逻辑类"),
        CreateGroup("基础类"),
        CreateGroup("键盘"),
        CreateGroup("鼠标操作"),
        CreateGroup("窗口与程序")
    ];

    private static ToolboxGroup CreateGroup(string category) =>
        new(category, Items.Where(item => item.Category == category).ToArray());
}
