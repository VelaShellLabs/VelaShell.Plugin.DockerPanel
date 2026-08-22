using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ContainersPageView 的视图。</summary>
public sealed partial class ContainersPageView : UserControl
{
    private double _rootWidth;

    /// <summary>建视图。</summary>
    public ContainersPageView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 面板一变宽窄就把抽屉的上限重算一遍。
    /// <para>
    /// 少了这一步,用户可以把抽屉拖得比面板还宽 —— 抽屉的头(还原 / 关闭)
    /// 会被顶到可视区外面,而那是回到列表的唯一入口。留 360px 给列表:
    /// 抽屉与列表同屏对照才是这个布局存在的理由,只剩一条缝的列表没有意义。
    /// </para>
    /// </summary>
    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _rootWidth = e.NewSize.Width;
        PushMaxDrawerWidth();
    }

    /// <summary>
    /// 上下文换人时补一次上限。
    /// <para>
    /// 尺寸只在**变化**时通知,而视图模型可能是量完之后才挂上来的 ——
    /// 那一次就没人告诉它面板有多宽,上限会停在默认值上。
    /// </para>
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        PushMaxDrawerWidth();
    }

    private void PushMaxDrawerWidth()
    {
        if (_rootWidth > 0 && DataContext is ContainersPageViewModel page)
        {
            page.MaxDrawerWidth = _rootWidth - 360;
        }
    }

    /// <summary>
    /// 点整行 = 打开详情抽屉。
    /// <para>
    /// 设计稿的行尾只有三颗动作按钮,没有单独的"详情"按钮 —— 行本身就是那颗按钮。
    /// 但行里还坐着勾选框和三颗动作按钮,它们的 Tapped 会一路冒泡到这里;
    /// 所以先看事件是不是从某个按钮里出来的,是就让开。
    /// </para>
    /// </summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source &&
            (source.FindAncestorOfType<Button>(true) is not null ||
             source.FindAncestorOfType<CheckBox>(true) is not null))
        {
            return;
        }
        if (sender is Control { DataContext: ContainerRow row } &&
            DataContext is ContainersPageViewModel page)
        {
            page.OpenDetailCommand.Execute(row);
        }
    }
}
