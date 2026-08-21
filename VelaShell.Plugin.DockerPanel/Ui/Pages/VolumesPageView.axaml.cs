using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>VolumesPageView 的视图。</summary>
public sealed partial class VolumesPageView : UserControl
{
    /// <summary>建视图。</summary>
    public VolumesPageView() => AvaloniaXamlLoader.Load(this);
}
