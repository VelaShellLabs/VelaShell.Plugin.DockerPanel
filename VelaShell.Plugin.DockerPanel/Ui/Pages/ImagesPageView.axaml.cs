using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ImagesPageView 的视图。</summary>
public sealed partial class ImagesPageView : UserControl
{
    /// <summary>建视图。</summary>
    public ImagesPageView() => AvaloniaXamlLoader.Load(this);
}
