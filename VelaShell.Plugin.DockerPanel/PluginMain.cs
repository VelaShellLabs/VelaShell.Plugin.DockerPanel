using VelaShell.Plugin.DockerPanel.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.DockerPanel;

/// <summary>
/// Docker 面板插件的入口。
/// <para>
/// 只做三件事:注册命令、按需开面板、停用时收干净。真正的东西都在
/// <see cref="DockerPanelViewModel" /> 与 <see cref="DockerPanelView" /> 里。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class DockerPanelPlugin : IVelaPlugin
{
    private IPluginContext? _context;
    private IPluginPanel? _panel;
    private DockerPanelViewModel? _viewModel;
    private IDisposable? _command;

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _command = context.Commands.Register(new(
            "velashell.dockerpanel.open",
            "Docker: 打开 Docker 管理面板",
            "Docker",
            _ => OpenPanelAsync()));
        // 激活即打开:用户是按了命令面板里那一条才走到这里的,再让他找一次入口没有道理。
        await OpenPanelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _command?.Dispose();
        _command = null;
        if (_panel is { } panel)
        {
            await panel.CloseAsync().ConfigureAwait(false);
            _panel = null;
        }
        if (_viewModel is { } viewModel)
        {
            // 事件流、日志流、统计流、隧道 —— 全都挂在这个对象上,它一走远端就干净了。
            await viewModel.DisposeAsync().ConfigureAwait(false);
            _viewModel = null;
        }
        _context = null;
    }

    private async Task OpenPanelAsync()
    {
        if (_context is not { } context)
        {
            return;
        }
        // 已经开着就把它带到眼前 —— 再开一个重复的不对,什么都不做又像是按钮坏了。
        if (_panel is { IsOpen: true } existing)
        {
            await existing.ActivateAsync().ConfigureAwait(false);
            return;
        }
        var viewModel = new DockerPanelViewModel(context);
        _viewModel = viewModel;
        try
        {
            _panel = await context.Ui.ShowPanelAsync(
                new() { Title = "Docker", DisplayMode = PanelDisplayMode.Document },
                () => new DockerPanelView(viewModel),
                context.Shutdown).ConfigureAwait(false);
            _panel.Closed += async () =>
            {
                _panel = null;
                await viewModel.DisposeAsync();
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    _viewModel = null;
                }
            };
            await viewModel.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Error($"failed to open the docker panel: {ex.Message}");
            await viewModel.DisposeAsync().ConfigureAwait(false);
            _viewModel = null;
        }
    }
}
