using Avalonia;
using Avalonia.Controls;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 按权重横向瓜分宽度的面板。
/// <para>
/// 每个子元素的 <c>Tag</c> 是它的权重(0–1 的比例);面板把可用宽度按权重分掉。
/// 用它而不是 <c>Grid</c> 的星形列,是因为段数是数据决定的 ——
/// <c>ColumnDefinitions</c> 绑不了集合。用它而不是固定像素,是因为面板宽度
/// 是用户拖出来的,算好的像素在下一次拖动就错了。
/// </para>
/// </summary>
public sealed class WeightedStackPanel : Panel
{
    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double total = 0;
        foreach (var child in Children)
        {
            total += Weight(child);
        }
        if (total <= 0)
        {
            return finalSize;
        }
        double x = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            // 最后一段吃掉舍入误差,免得右边留一条一像素的缝。
            var width = i == Children.Count - 1
                ? Math.Max(0, finalSize.Width - x)
                : finalSize.Width * (Weight(child) / total);
            child.Arrange(new(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }

    private static double Weight(Control child) =>
        child.Tag is double weight && weight > 0 ? weight
        : child.Tag is string text && double.TryParse(text, out var parsed) && parsed > 0 ? parsed
        : 0;
}
