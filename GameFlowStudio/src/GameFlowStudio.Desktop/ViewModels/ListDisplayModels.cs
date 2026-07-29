using GameFlowStudio.Contracts.Enums;

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

public sealed record WindowExecutionModeOption(WindowExecutionMode Value, string Label, string Description);

public sealed class ScriptListItem(string id, string name) : ObservableObject
{
    private bool _isActive;
    private string _name = name;

    public string Id { get; } = id;
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}
