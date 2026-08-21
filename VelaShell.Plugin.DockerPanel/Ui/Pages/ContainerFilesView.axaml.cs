using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ContainerFilesView 的视图。</summary>
public sealed partial class ContainerFilesView : UserControl
{
    /// <summary>建视图。</summary>
    public ContainerFilesView() => AvaloniaXamlLoader.Load(this);
}
