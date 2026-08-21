using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ContainersPageView 的视图。</summary>
public sealed partial class ContainersPageView : UserControl
{
    /// <summary>建视图。</summary>
    public ContainersPageView() => AvaloniaXamlLoader.Load(this);
}
