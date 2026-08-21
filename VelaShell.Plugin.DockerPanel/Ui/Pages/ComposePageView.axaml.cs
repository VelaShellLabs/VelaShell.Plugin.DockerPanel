using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ComposePageView 的视图。</summary>
public sealed partial class ComposePageView : UserControl
{
    /// <summary>建视图。</summary>
    public ComposePageView() => AvaloniaXamlLoader.Load(this);
}
