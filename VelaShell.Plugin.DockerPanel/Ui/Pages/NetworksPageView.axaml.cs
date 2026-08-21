using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>NetworksPageView 的视图。</summary>
public sealed partial class NetworksPageView : UserControl
{
    /// <summary>建视图。</summary>
    public NetworksPageView() => AvaloniaXamlLoader.Load(this);
}
