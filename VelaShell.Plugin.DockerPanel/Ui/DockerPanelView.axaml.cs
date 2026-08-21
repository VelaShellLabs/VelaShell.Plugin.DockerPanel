using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// Docker 面板的视图。
/// <para>
/// 代码里只做一件事:把 <c>ListBox</c> 的多选结果推给视图模型。
/// Avalonia 的 <c>SelectedItems</c> 不是可绑定的 <c>StyledProperty</c>(它是普通 CLR 属性),
/// 多选列表因此只能走事件 —— 这是控件的形状决定的,不是偷懒。
/// 其余一切(可用性、可见性、文案、命令)都在 AXAML 里绑,视图不持有任何状态。
/// </para>
/// </summary>
public sealed partial class DockerPanelView : UserControl
{
    private readonly DockerPanelViewModel _viewModel;

    /// <summary>构造。</summary>
    /// <param name="viewModel">视图模型(由面板工厂在 UI 线程注入)。</param>
    /// <summary>抽屉在 <c>RootGrid</c> 里的行号。</summary>
    private const int DrawerRowIndex = 6;

    public DockerPanelView(DockerPanelViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        BindDrawerHeight(viewModel);
    }

    /// <summary>
    /// 把抽屉那一行的高度**双向**接到视图模型。
    /// <para>
    /// 为什么不在 AXAML 里写 <c>&lt;RowDefinition Height="{Binding …}"/&gt;</c>:
    /// <c>RowDefinition</c> 不是可视树里的控件,DataContext 会不会流到它身上是随实现走的 ——
    /// 编译期不会报错,运行期只会得到一条永远解析不出来的绑定,表现为"抽屉收不起来"。
    /// 这里显式给 <c>Source</c>,不依赖任何继承。
    /// </para>
    /// <para>
    /// 双向是必须的:用户拖分割条改的是 <c>RowDefinition.Height</c>,那个新高度要回到视图模型,
    /// 下次展开抽屉才回得到用户拖出来的位置。
    /// </para>
    /// </summary>
    /// <param name="viewModel">视图模型。</param>
    private void BindDrawerHeight(DockerPanelViewModel viewModel)
    {
        if (RootGrid.RowDefinitions.Count <= DrawerRowIndex)
        {
            return;
        }
        RootGrid.RowDefinitions[DrawerRowIndex].Bind(
            RowDefinition.HeightProperty,
            new Binding(nameof(DockerPanelViewModel.DrawerHeight))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay
            });
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnContainerSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _viewModel.SetContainerSelection(Selected<ContainerRow>(sender));

    private void OnImageSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _viewModel.SetImageSelection(Selected<ImageRow>(sender));

    private void OnVolumeSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _viewModel.SetVolumeSelection(Selected<VolumeRow>(sender));

    private void OnNetworkSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _viewModel.SetNetworkSelection(Selected<NetworkRow>(sender));

    /// <summary>
    /// 取列表当前选中的行。
    /// <para>
    /// 按**列表里的顺序**取,而不是按用户点击的顺序 —— 后者是 <c>SelectedItems</c> 的实际顺序,
    /// 但确认框里"要删这些:a, b, c"如果和屏幕上的排列对不上,人是核对不了的。
    /// </para>
    /// </summary>
    /// <typeparam name="T">行类型。</typeparam>
    /// <param name="sender">列表控件。</param>
    /// <returns>选中的行。</returns>
    private static IReadOnlyList<T> Selected<T>(object? sender) where T : class
    {
        if (sender is not ListBox { SelectedItems: { } selected } list)
        {
            return [];
        }
        List<T> rows = [];
        foreach (object? item in list.Items)
        {
            if (item is T typed && selected.Contains(item))
            {
                rows.Add(typed);
            }
        }
        return rows;
    }
}
