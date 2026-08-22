using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ImagesPageView 的视图。</summary>
public sealed partial class ImagesPageView : UserControl
{
    /// <summary>建视图。</summary>
    public ImagesPageView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 点整行 = 打开详情抽屉。行尾那几颗动作按钮的 Tapped 会一路冒泡到这里,
    /// 所以先看事件是不是从某个按钮里出来的,是就让开。
    /// </summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source &&
            (source.FindAncestorOfType<Button>(true) is not null ||
             source.FindAncestorOfType<CheckBox>(true) is not null))
        {
            return;
        }
        if (sender is Control { DataContext: ImageRow row } && DataContext is ImagesPageViewModel page)
        {
            page.OpenDetailCommand.Execute(row);
        }
    }
}
