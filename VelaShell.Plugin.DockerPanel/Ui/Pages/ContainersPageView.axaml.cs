using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ContainersPageView 的视图。</summary>
public sealed partial class ContainersPageView : UserControl
{
    /// <summary>建视图。</summary>
    public ContainersPageView() => AvaloniaXamlLoader.Load(this);

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
