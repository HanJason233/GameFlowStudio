using System.Windows;
using System.Windows.Controls;

namespace GameFlowStudio.Desktop.Controls;

/// <summary>只读文本控件，支持鼠标拖动选择和复制。</summary>
public sealed class SelectableTextBlock : TextBox
{
    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming),
        typeof(TextTrimming),
        typeof(SelectableTextBlock),
        new FrameworkPropertyMetadata(TextTrimming.None));

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }
}
