using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 面板视图。
/// <para>
/// 代码后置只做一件事:把几个"参数是数据对象、目标是普通委托"的点击接回视图模型 ——
/// 这些回调(恢复动作、toast 动作、取消某个任务)天生就是 <c>Action</c>,
/// 为它们各造一个 <c>ICommand</c> 只是为了讨好 XAML,不值得。
/// </para>
/// </summary>
public sealed partial class DockerPanelView : UserControl
{
    /// <summary>建视图并挂上视图模型。</summary>
    public DockerPanelView(DockerPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // 文件选择器要一个顶层窗口,而只有视图知道自己被画在哪个窗口里。
        FilePicker.Attach(() => TopLevel.GetTopLevel(this));
    }

    /// <summary>无参构造只为设计器与 XAML 装载器;运行时走带上下文的那个。</summary>
    public DockerPanelView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 面板级快捷键。
    /// <para>
    /// 只在**面板拿到焦点**时生效(<c>OnKeyDown</c> 走的是面板自己的可视树),
    /// 不注册全局热键 —— 宿主自己也有 <c>Ctrl+K</c>,抢它的键会让用户在别处按不出宿主的命令面板。
    /// </para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not DockerPanelViewModel viewModel || !viewModel.IsReady)
        {
            base.OnKeyDown(e);
            return;
        }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.K)
        {
            viewModel.OpenPaletteCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // 行级快捷键落在当前选中的那一行上;没有选中就什么都不做,
        // 而不是猜一个 —— 对着错的容器执行"停止"比没反应糟得多。
        if (ctrl && viewModel.Containers.Detail is { } detail)
        {
            switch (e.Key)
            {
                case Key.R:
                    detail.RestartCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemPeriod:
                    detail.StopCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }
        base.OnKeyDown(e);
    }

    private void OnRecoveryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: RecoveryAction action })
        {
            action.Invoke();
        }
    }

    private void OnToastAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ToastAction action })
        {
            action.Invoke();
        }
    }

    private void OnDismissToast(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: Toast toast } && DataContext is DockerPanelViewModel viewModel)
        {
            viewModel.Feedback.Dismiss(toast);
        }
    }

    private void OnCancelTask(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: PanelTask task })
        {
            task.Cancel();
        }
    }
}
