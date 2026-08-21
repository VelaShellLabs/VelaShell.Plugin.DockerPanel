using Avalonia.Threading;
using VelaShell.Plugin.DockerPanel.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.DockerPanel;

/// <summary>
/// Docker 面板插件的入口。
/// <para>
/// 经 manifest 的 <c>onCommand:velashell.dockerpanel.open</c> **惰性激活**:命令面板里那一条
/// 在发现期就在了(不装载本程序集),用户按下它才把插件拉起来。
/// </para>
/// <para>
/// **这个插件没有自己的连接类型**,这是刻意的。管理远端 docker 不需要第二条连接:
/// 用户已经有一条 SSH 会话了,面板复用它的 exec 通道去跑 <c>docker</c>。
/// 于是既不用在服务器上把 daemon 暴露到 TCP(那是一个 root 等价的无认证端口),
/// 也不用让插件碰一次凭据。代价是面板只能管**已经连上的**主机 —— 这正是 v1 插件
/// 契约里的边界(插件不能自行发起连接),而不是一个绕过它的理由。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class DockerPanelPlugin : IVelaPlugin
{
    /// <summary>命令 id(必须与 <c>plugin.json</c> 的占位命令一致,激活时替换掉占位)。</summary>
    private const string OpenCommandId = "velashell.dockerpanel.open";

    private readonly SemaphoreSlim _openGate = new(1, 1);
    private IPluginContext? _context;
    private IDisposable? _commandRegistration;
    private IPluginPanel? _panel;
    private DockerPanelViewModel? _viewModel;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        Register(context);
        // 语言切换后重注册:命令标题是插件自己的文案,宿主不会替我们翻。
        // 已经开着的面板不动 —— 它的文案在自己的视图模型里,重开一次的代价太大。
        context.Events.LocaleChanged += _ => Register(context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _commandRegistration?.Dispose();
        _commandRegistration = null;
        // 面板由宿主在插件停用时自动关闭,但视图模型里那个后台刷新循环是我们自己的 ——
        // 不收掉它,ALC 就回收不了这个程序集。
        if (_viewModel is { } viewModel)
        {
            _viewModel = null;
            await viewModel.DisposeAsync().ConfigureAwait(false);
        }
        _panel = null;
        _context = null;
        _openGate.Dispose();
    }

    private void Register(IPluginContext context)
    {
        Loc loc = new(context.Host.Locale);
        // **不要**在这里释放上一个句柄。命令注册表是按 id 索引的:`Register` 本身就是
        // "同 id 则替换",而句柄的 `Dispose` 是"按 id 移除" —— 注册新的再释放旧的,
        // 会把刚放进去的那一条一起删掉,命令面板里就什么都不剩了(协议/工作台注册表
        // 是按实例索引的,那里"先注册后释放"才对,别把两者的直觉混用)。
        _commandRegistration = context.Commands.Register(new PluginCommandDescriptor(
            OpenCommandId,
            loc["Command_Open"],
            loc["Command_Category"],
            OpenPanelAsync));
    }

    /// <summary>
    /// 打开(或激活)面板。
    /// <para>
    /// 命令体在**后台线程**执行,所以这里一步都不能直接碰控件:
    /// 视图模型的构造与首次装载都封送到 UI 线程,面板内容工厂本来就由宿主在 UI 线程调用。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task OpenPanelAsync(CancellationToken cancellationToken)
    {
        if (_context is not { } context)
        {
            return;
        }
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 已经开着就把它带到眼前,而不是再开一个 —— 两个一模一样的 Docker 标签页
            // 各自跑着自己的刷新循环,是把远端敲两遍的最快办法。
            if (_panel is { IsOpen: true } existing)
            {
                await existing.ActivateAsync().ConfigureAwait(false);
                return;
            }
            Loc loc = new(context.Host.Locale);
            // GetTask():带返回值的 InvokeAsync 回的是 DispatcherOperation<T>,它本身可 await
            // 但没有 ConfigureAwait —— 取出里面的 Task 再等,才能明确"不必回到调用线程"。
            DockerPanelViewModel viewModel = await Dispatcher.UIThread
                                                             .InvokeAsync(() => new DockerPanelViewModel(context, loc))
                                                             .GetTask()
                                                             .ConfigureAwait(false);
            IPluginPanel panel = await context.Ui.ShowPanelAsync(
                new PanelOptions
                {
                    Title = loc["Panel_Title"],
                    DisplayMode = PanelDisplayMode.Document
                },
                () => new DockerPanelView(viewModel),
                cancellationToken).ConfigureAwait(false);
            _panel = panel;
            _viewModel = viewModel;
            panel.Closed += () => OnPanelClosed(viewModel);
            await Dispatcher.UIThread.InvokeAsync(viewModel.InitializeAsync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Error("Opening the Docker panel failed.", ex);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private void OnPanelClosed(DockerPanelViewModel viewModel)
    {
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }
        _viewModel = null;
        _panel = null;
        // Closed 是同步事件:在这里 await 会把宿主关标签页的那条路径挂住。
        _ = Task.Run(async () =>
        {
            try
            {
                await viewModel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _context?.Log.Warn("Disposing the Docker panel view model failed.", ex);
            }
        });
    }
}
