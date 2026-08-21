using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui.Pages;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>面板与 daemon 的连接状态。</summary>
public enum PanelConnectionState
{
    /// <summary>还没选端点。</summary>
    NoEndpoint,

    /// <summary>正在建立通道。</summary>
    Connecting,

    /// <summary>连上了。</summary>
    Ready,

    /// <summary>连不上。</summary>
    Failed
}

/// <summary>主机切换器里的一项。</summary>
public sealed class EndpointItem(DockerEndpoint endpoint, bool available, string stateText, FeedbackKind stateKind)
    : ObservableObject
{
    /// <summary>端点。</summary>
    public DockerEndpoint Endpoint { get; } = endpoint;

    /// <summary>显示名。</summary>
    public string DisplayName => Endpoint.DisplayName;

    /// <summary>小字(user@host / 管道路径)。</summary>
    public string Detail => Endpoint.Detail;

    /// <summary>是不是本机。</summary>
    public bool IsLocal => Endpoint.Kind == DockerEndpointKind.Local;

    /// <summary>能不能选(会话断了、找不到 socket 的项置灰)。</summary>
    public bool Available { get; private set; } = available;

    /// <summary>右侧的状态短语。</summary>
    public string StateText { get; private set; } = stateText;

    /// <summary>状态短语的语气。</summary>
    public FeedbackKind StateKind { get; private set; } = stateKind;

    /// <summary>图标资源键。</summary>
    public string Icon => IsLocal ? "Docker.monitor" : "Docker.server";

    /// <summary>更新状态。</summary>
    public void Update(bool available, string stateText, FeedbackKind stateKind)
    {
        Available = available;
        StateText = stateText;
        StateKind = stateKind;
        OnPropertiesChanged(nameof(Available), nameof(StateText), nameof(StateKind));
    }
}

/// <summary>连不上时给用户的一条出路。</summary>
/// <param name="Label">按钮文字。</param>
/// <param name="Icon">图标资源键。</param>
/// <param name="Primary">是不是主要按钮。</param>
/// <param name="Invoke">回调。</param>
public sealed record RecoveryAction(string Label, string Icon, bool Primary, Action Invoke);

/// <summary>
/// 面板外壳的视图模型:端点、导航、连接生命周期与事件驱动刷新。
/// <para>
/// 页面自己不拉数据也不挂定时器 —— 刷新的时机全部由这里按 <c>docker events</c> 决定,
/// 一条事件流喂饱所有页面。
/// </para>
/// </summary>
public sealed partial class DockerPanelViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPluginContext _context;
    private string _socketPathInput = "";
    private int _countContainers;
    private int _countImages;
    private int _countVolumes;
    private readonly CancellationTokenSource _lifetime = new();

    private EndpointItem? _selectedEndpoint;
    private DockerClient? _client;
    private ComposeCli? _compose;
    private RegistryAuthProvider? _registryAuth;
    private PanelConnectionState _state = PanelConnectionState.NoEndpoint;
    private string _errorTitle = "";
    private string _errorDetail = "";
    private string _errorHint = "";
    private string _errorIcon = "Icon.circle-alert";
    private PanelPage _currentPage = PanelPage.Containers;
    private PageViewModel? _activePage;
    private string _engineVersion = "";
    private string _apiVersion = "";
    private string _countsText = "";
    private bool _endpointMenuOpen;
    private bool _taskCenterOpen;
    private bool _settingsOpen;

    /// <summary>建外壳。</summary>
    public DockerPanelViewModel(IPluginContext context)
    {
        _context = context;
        Settings = new(context.Storage);
        Confirm = new();
        Tasks = new();
        Feedback = new();
        Overview = new OverviewPageViewModel(this);
        Containers = new ContainersPageViewModel(this);
        Images = new ImagesPageViewModel(this);
        Volumes = new VolumesPageViewModel(this);
        Networks = new NetworksPageViewModel(this);
        ComposePage = new ComposePageViewModel(this);
        SystemPage = new SystemPageViewModel(this);
        AllPages = [Overview, Containers, Images, Volumes, Networks, ComposePage, SystemPage];
        _activePage = Containers;

        SelectPageCommand = new RelayCommand(p =>
        {
            if (p is PanelPage page)
            {
                _ = GoToAsync(page);
            }
        });
        RefreshCommand = new RelayCommand(_ => RefreshActiveAsync(force: true), _ => IsReady);
        ReconnectCommand = new RelayCommand(_ => ConnectAsync(_selectedEndpoint));
        SelectEndpointCommand = new RelayCommand(p =>
        {
            EndpointMenuOpen = false;
            return p is EndpointItem item && item.Available ? ConnectAsync(item) : Task.CompletedTask;
        });
        ToggleEndpointMenuCommand = new RelayCommand(_ => EndpointMenuOpen = !EndpointMenuOpen);
        ToggleTaskCenterCommand = new RelayCommand(_ => TaskCenterOpen = !TaskCenterOpen);
        ToggleSettingsCommand = new RelayCommand(_ => SettingsOpen = !SettingsOpen);
        Palette = new(CollectPaletteEntries);
        OpenPaletteCommand = new RelayCommand(_ =>
        {
            Palette.Open(SelectedEndpoint?.DisplayName ?? "(未选择)");
            return Task.CompletedTask;
        });
        ApplySocketPathCommand = new RelayCommand(_ => ApplySocketPathAsync());
        ResetSocketPathCommand = new RelayCommand(_ =>
        {
            SocketPathInput = SocketPathDefault;
            return ApplySocketPathAsync();
        });
        ClearFinishedTasksCommand = new RelayCommand(_ => Tasks.ClearFinished());
        DismissToastCommand = new RelayCommand(p =>
        {
            if (p is Toast toast)
            {
                Feedback.Dismiss(toast);
            }
        });

        RelayCommand.UnhandledCommandError += ex => Feedback.ReportError("操作", ex);
        Settings.Changed += () =>
        {
            _ = Settings.SaveAsync(_lifetime.Token);
            // 关掉"实时统计"要立刻停下采样,而不是等下次切页 ——
            // 那个开关的整个卖点就是省远端开销。
            if (IsReady)
            {
                Containers.StartSampling();
            }
        };
    }

    /// <summary>宿主上下文。</summary>
    public IPluginContext Context => _context;

    /// <summary>面板设置。</summary>
    public PanelSettings Settings { get; }

    /// <summary>确认闸门。</summary>
    public ConfirmGate Confirm { get; }

    /// <summary>任务中心。</summary>
    public TaskCenter Tasks { get; }

    /// <summary>结果反馈(状态栏 + toast)。</summary>
    public Feedback Feedback { get; }

    /// <summary>当前端点的客户端;没连上时为 <see langword="null" />。</summary>
    public DockerClient? Client => _client;

    /// <summary>Compose(只有远端端点有)。</summary>
    public ComposeCli? Compose => _compose;

    /// <summary>仓库凭据。</summary>
    public RegistryAuthProvider? RegistryAuth => _registryAuth;

    /// <summary>面板生命周期令牌。</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    // ── 端点 ──────────────────────────────────────────────────────

    /// <summary>可选的端点。</summary>
    public ObservableCollection<EndpointItem> Endpoints { get; } = [];

    /// <summary>当前端点。</summary>
    public EndpointItem? SelectedEndpoint
    {
        get => _selectedEndpoint;
        private set
        {
            if (SetField(ref _selectedEndpoint, value))
            {
                SocketPathInput = value?.Endpoint.SocketPath ?? "";
                OnPropertiesChanged(nameof(EndpointName), nameof(EndpointDetail), nameof(HasEndpoint),
                    nameof(ComposeAvailable), nameof(SocketPathDefault), nameof(SocketPathChanged));
            }
        }
    }

    /// <summary>顶栏显示的主机名。</summary>
    public string EndpointName => SelectedEndpoint?.DisplayName ?? "选择目标";

    /// <summary>顶栏主机名后面那行小字。</summary>
    public string EndpointDetail => SelectedEndpoint is { } item
        ? $"{item.Endpoint.SocketPath} · {(item.IsLocal ? "本机" : "SSH 隧道")}"
        : "";

    /// <summary>选了端点没有。</summary>
    public bool HasEndpoint => SelectedEndpoint is not null;

    /// <summary>
    /// 设置抽屉里那个 socket 路径输入框。
    /// <para>
    /// 存在的理由:"这台机器上找不到 docker.sock"的补救动作就是换一条路径
    /// (rootless Docker 在 <c>$XDG_RUNTIME_DIR/docker.sock</c>,Colima、OrbStack 各有各的位置)。
    /// 补救按钮把设置抽屉打开却没有这个框,等于把用户领到一堵墙前面。
    /// </para>
    /// </summary>
    public string SocketPathInput
    {
        get => _socketPathInput;
        set
        {
            if (SetField(ref _socketPathInput, value))
            {
                OnPropertyChanged(nameof(SocketPathChanged));
            }
        }
    }

    /// <summary>当前端点默认的 socket 路径(用来判断"是不是改过了")。</summary>
    public string SocketPathDefault =>
        SelectedEndpoint?.Endpoint.Kind == DockerEndpointKind.Local && OperatingSystem.IsWindows()
            ? DockerEndpoint.DefaultWindowsPipe
            : DockerEndpoint.DefaultUnixSocket;

    /// <summary>改过 socket 路径没有(决定"恢复默认"要不要亮)。</summary>
    public bool SocketPathChanged =>
        SocketPathInput.Trim().Length > 0 && SocketPathInput.Trim() != SocketPathDefault;

    /// <summary>主机切换器是否展开。</summary>
    public bool EndpointMenuOpen
    {
        get => _endpointMenuOpen;
        set => SetField(ref _endpointMenuOpen, value);
    }

    /// <summary>compose 页可不可用(本机端点没有)。</summary>
    public bool ComposeAvailable => SelectedEndpoint?.Endpoint.SupportsCompose == true;

    // ── 连接状态 ──────────────────────────────────────────────────

    /// <summary>连接状态。</summary>
    public PanelConnectionState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertiesChanged(nameof(IsConnecting), nameof(IsReady), nameof(IsFailed), nameof(NeedsEndpoint));
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>还没选端点。</summary>
    public bool NeedsEndpoint => State == PanelConnectionState.NoEndpoint;

    /// <summary>正在建立通道。</summary>
    public bool IsConnecting => State == PanelConnectionState.Connecting;

    /// <summary>连上了。</summary>
    public bool IsReady => State == PanelConnectionState.Ready;

    /// <summary>连不上。</summary>
    public bool IsFailed => State == PanelConnectionState.Failed;

    /// <summary>连不上时的标题。</summary>
    public string ErrorTitle
    {
        get => _errorTitle;
        private set => SetField(ref _errorTitle, value);
    }

    /// <summary>连不上时的正文。</summary>
    public string ErrorDetail
    {
        get => _errorDetail;
        private set => SetField(ref _errorDetail, value);
    }

    /// <summary>连不上时下面那行等宽小字(daemon 的原话)。</summary>
    public string ErrorHint
    {
        get => _errorHint;
        private set => SetField(ref _errorHint, value);
    }

    /// <summary>连不上时的图标。</summary>
    public string ErrorIcon
    {
        get => _errorIcon;
        private set => SetField(ref _errorIcon, value);
    }

    /// <summary>连不上时给的出路。</summary>
    public ObservableCollection<RecoveryAction> RecoveryActions { get; } = [];

    // ── 导航 ──────────────────────────────────────────────────────

    /// <summary>总览页。</summary>
    public OverviewPageViewModel Overview { get; }

    /// <summary>容器页。</summary>
    public ContainersPageViewModel Containers { get; }

    /// <summary>镜像页。</summary>
    public ImagesPageViewModel Images { get; }

    /// <summary>卷页。</summary>
    public VolumesPageViewModel Volumes { get; }

    /// <summary>网络页。</summary>
    public NetworksPageViewModel Networks { get; }

    /// <summary>Compose 页。</summary>
    public ComposePageViewModel ComposePage { get; }

    /// <summary>系统页。</summary>
    public SystemPageViewModel SystemPage { get; }

    /// <summary>全部页面。</summary>
    public IReadOnlyList<PageViewModel> AllPages { get; }

    /// <summary>当前页标识(左导航栏据此选中)。</summary>
    public PanelPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetField(ref _currentPage, value))
            {
                OnPropertiesChanged(nameof(IsOverview), nameof(IsContainers), nameof(IsImages),
                    nameof(IsVolumes), nameof(IsNetworks), nameof(IsCompose), nameof(IsSystem));
            }
        }
    }

    /// <summary>当前页的视图模型。</summary>
    public PageViewModel? ActivePage
    {
        get => _activePage;
        private set => SetField(ref _activePage, value);
    }

    /// <summary>当前是总览页。</summary>
    public bool IsOverview => CurrentPage == PanelPage.Overview;

    /// <summary>当前是容器页。</summary>
    public bool IsContainers => CurrentPage == PanelPage.Containers;

    /// <summary>当前是镜像页。</summary>
    public bool IsImages => CurrentPage == PanelPage.Images;

    /// <summary>当前是卷页。</summary>
    public bool IsVolumes => CurrentPage == PanelPage.Volumes;

    /// <summary>当前是网络页。</summary>
    public bool IsNetworks => CurrentPage == PanelPage.Networks;

    /// <summary>当前是 Compose 页。</summary>
    public bool IsCompose => CurrentPage == PanelPage.Compose;

    /// <summary>当前是系统页。</summary>
    public bool IsSystem => CurrentPage == PanelPage.System;

    // ── 状态栏 ────────────────────────────────────────────────────

    /// <summary>daemon 版本。</summary>
    public string EngineVersion
    {
        get => _engineVersion;
        private set => SetField(ref _engineVersion, value);
    }

    /// <summary>API 版本。</summary>
    public string ApiVersion
    {
        get => _apiVersion;
        private set => SetField(ref _apiVersion, value);
    }

    /// <summary>“18 容器 · 34 镜像 · 9 卷”。</summary>
    public string CountsText
    {
        get => _countsText;
        private set => SetField(ref _countsText, value);
    }

    /// <summary>任务中心弹层是否展开。</summary>
    public bool TaskCenterOpen
    {
        get => _taskCenterOpen;
        set => SetField(ref _taskCenterOpen, value);
    }

    /// <summary>设置抽屉是否展开。</summary>
    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => SetField(ref _settingsOpen, value);
    }

    // ── 命令 ──────────────────────────────────────────────────────

    /// <summary>切页。</summary>
    public RelayCommand SelectPageCommand { get; }

    /// <summary>手动刷新当前页。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>重连。</summary>
    public RelayCommand ReconnectCommand { get; }

    /// <summary>选一个端点。</summary>
    public RelayCommand SelectEndpointCommand { get; }

    /// <summary>展开/收起主机切换器。</summary>
    public RelayCommand ToggleEndpointMenuCommand { get; }

    /// <summary>展开/收起任务中心。</summary>
    public RelayCommand ToggleTaskCenterCommand { get; }

    /// <summary>展开/收起设置。</summary>
    public RelayCommand ToggleSettingsCommand { get; }

    /// <summary>面板内的命令面板(<c>Ctrl+K</c>)。</summary>
    public CommandPalette Palette { get; }

    /// <summary>打开命令面板。</summary>
    public RelayCommand OpenPaletteCommand { get; }

    /// <summary>
    /// 收集当前能做的事。
    /// <para>
    /// 每次打开都重新收集,不缓存 —— 容器列表随时在变,一份缓存下来的命令列表
    /// 会让用户对着一个已经不存在的容器按回车。
    /// </para>
    /// </summary>
    private IReadOnlyList<PaletteEntry> CollectPaletteEntries()
    {
        List<PaletteEntry> entries = [];
        if (!IsReady)
        {
            return entries;
        }

        // ── 动作:对具体容器的高频操作。放最前面,因为它们是"要做一件事"而不是"要看一眼"。
        foreach (ContainerRow row in Containers.View.Where(r => r.IsRunning).Take(20))
        {
            ContainerRow target = row;
            entries.Add(new("动作", $"重启 {target.Name}", DescribeContainer(target), "Docker.rotate-cw",
                RowTone.Ok, false, () => { Containers.RestartCommand.Execute(target); return Task.CompletedTask; }));
            entries.Add(new("动作", $"停止 {target.Name}", DescribeContainer(target), "Icon.square",
                RowTone.Idle, false, () => { Containers.StopCommand.Execute(target); return Task.CompletedTask; }));
        }

        // ── 容器 / 镜像 / 卷:导航到某个对象。
        foreach (ContainerRow row in Containers.View.Take(40))
        {
            ContainerRow target = row;
            entries.Add(new("容器", target.Name, DescribeContainer(target), "Docker.box",
                target.Tone, false, () => Containers.OpenDetailCommand is { } open
                    ? Task.Run(() => Ui.Post(() => open.Execute(target)))
                    : Task.CompletedTask));
        }
        foreach (ImageRow row in Images.View.Take(40))
        {
            ImageRow target = row;
            entries.Add(new("镜像 / 卷", $"{target.Repository}:{target.Tag}", $"镜像 · {target.SizeText}",
                "Icon.layers", RowTone.Idle, false,
                () => { Images.OpenDetailCommand.Execute(target); return Task.CompletedTask; }));
        }
        foreach (VolumeRow row in Volumes.View.Take(40))
        {
            VolumeRow target = row;
            entries.Add(new("镜像 / 卷", target.Name, $"卷 · {target.SizeText}", "Icon.hard-drive",
                RowTone.Idle, false, () => { Volumes.SelectCommand.Execute(target); return Task.CompletedTask; }));
        }

        // ── 面板命令:导航与全局动作。破坏性的带省略号,选中后仍走闸门。
        entries.Add(new("面板命令", "拉取镜像…", "从仓库拉一个镜像", "Docker.arrow-down-to-line",
            RowTone.Idle, false, () => ShowPullDialogAsync(null)));
        entries.Add(new("面板命令", "清理未使用的镜像…", "释放磁盘,重新拉要花时间与带宽", "Docker.broom",
            RowTone.Warn, true, () => { Images.PruneAllCommand.Execute(null); return Task.CompletedTask; }));
        entries.Add(new("面板命令", "清理悬空镜像…", "只删没有标签也没人用的中间层", "Docker.broom",
            RowTone.Idle, true, () => { Images.PruneDanglingCommand.Execute(null); return Task.CompletedTask; }));
        entries.Add(new("面板命令", "打开设置", "连接、显示、行为", "Icon.settings",
            RowTone.Idle, false, () => { SettingsOpen = true; return Task.CompletedTask; }));
        foreach ((PanelPage page, string title, string icon) in ((PanelPage, string, string)[])
                 [
                     (PanelPage.Overview, "总览", "Docker.layout-dashboard"),
                     (PanelPage.Containers, "容器", "Docker.box"),
                     (PanelPage.Images, "镜像", "Icon.layers"),
                     (PanelPage.Volumes, "卷", "Icon.hard-drive"),
                     (PanelPage.Networks, "网络", "Icon.network"),
                     (PanelPage.System, "系统", "Icon.gauge")
                 ])
        {
            PanelPage target = page;
            entries.Add(new("面板命令", $"转到{title}", "切换页面", icon, RowTone.Idle, false,
                () => GoToAsync(target)));
        }
        return entries;
    }

    private static string DescribeContainer(ContainerRow row) =>
        row.HasProject ? $"{(row.IsRunning ? "运行中" : row.Uptime)} · {row.Project}"
            : $"{(row.IsRunning ? "运行中" : row.Uptime)} · {row.Image}";

    /// <summary>用输入框里的路径重连。</summary>
    public RelayCommand ApplySocketPathCommand { get; }

    /// <summary>恢复默认 socket 路径并重连。</summary>
    public RelayCommand ResetSocketPathCommand { get; }

    /// <summary>清掉已完成的任务。</summary>
    public RelayCommand ClearFinishedTasksCommand { get; }

    /// <summary>关掉一条 toast。</summary>
    public RelayCommand DismissToastCommand { get; }

    // ── 生命周期 ──────────────────────────────────────────────────

    /// <summary>面板打开时调用:读设置、列端点、自动连上唯一那个。</summary>
    public async Task InitializeAsync()
    {
        await Settings.LoadAsync(_lifetime.Token).ConfigureAwait(true);
        _context.Events.SessionConnected += OnSessionChanged;
        _context.Events.SessionDisconnected += OnSessionChanged;
        await ReloadEndpointsAsync().ConfigureAwait(true);
        // 只有一个可用端点时直接连上 —— 让用户为一个没得选的选择再点一次没有意义。
        EndpointItem[] usable = [.. Endpoints.Where(e => e.Available)];
        if (usable.Length == 1)
        {
            await ConnectAsync(usable[0]).ConfigureAwait(true);
        }
    }

    /// <summary>重新列一遍端点(会话连上/断开时也会走这里)。</summary>
    public async Task ReloadEndpointsAsync()
    {
        IReadOnlyList<SessionInfo> sessions;
        try
        {
            sessions = await _context.Sessions.ListAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sessions = [];
        }
        List<EndpointItem> items =
        [
            new(DockerEndpoint.Local("本机 Docker"), true, "可用", FeedbackKind.Info)
        ];
        foreach (SessionInfo session in sessions.OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase))
        {
            bool connected = session.State == SessionState.Connected;
            items.Add(new(
                DockerEndpoint.Remote(session.SessionId, session.Host, $"{session.Username}@{session.Host}:{session.Port}"),
                connected,
                connected ? "已连接" : "未连接",
                connected ? FeedbackKind.Success : FeedbackKind.Info));
        }
        Ui.Post(() =>
        {
            Endpoints.Clear();
            foreach (EndpointItem item in items)
            {
                Endpoints.Add(item);
            }
            if (State == PanelConnectionState.NoEndpoint)
            {
                SetNoEndpoint(sessions.Count(s => s.State == SessionState.Connected));
            }
        });
    }

    private void OnSessionChanged(SessionInfo session) => _ = ReloadEndpointsAsync();

    /// <summary>
    /// 连到一个端点:建客户端 → 探一次 <c>/version</c> → 起事件流 → 刷新当前页。
    /// </summary>
    public async Task ConnectAsync(EndpointItem? item)
    {
        if (item is null)
        {
            return;
        }
        await StopEventStreamAsync().ConfigureAwait(true);
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(true);
            _client = null;
        }
        foreach (PageViewModel page in AllPages)
        {
            page.Reset();
        }
        SelectedEndpoint = item;
        State = PanelConnectionState.Connecting;
        RecoveryActions.Clear();

        DockerEndpoint endpoint = item.Endpoint;
        string? remembered = await Settings.GetSocketPathAsync(endpoint.DisplayName, _lifetime.Token).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(remembered) && remembered != endpoint.SocketPath)
        {
            endpoint = endpoint with { SocketPath = remembered };
        }
        // 输入框显示的是**实际用的**那条路径,而不是端点的出厂默认值。
        SocketPathInput = endpoint.SocketPath;
        IDockerTransport transport = endpoint.Kind == DockerEndpointKind.Local
            ? new LocalTransport(endpoint.SocketPath)
            : new TunnelTransport(_context.RemoteTunnel, endpoint.SessionId, endpoint.SocketPath);
        var client = new DockerClient(endpoint, transport);
        try
        {
            SystemVersion version = await client.PingAsync(_lifetime.Token).ConfigureAwait(true);
            _client = client;
            _registryAuth = new(_context.RemoteFs, endpoint);
            _compose = endpoint.SupportsCompose
                ? new ComposeCli(_context.RemoteExec, _context.RemoteFs, endpoint.SessionId)
                : null;
            EngineVersion = version.Version is { Length: > 0 } v ? $"Engine {v}" : "";
            ApiVersion = version.ApiVersion is { Length: > 0 } a ? $"API v{a}" : "";
            State = PanelConnectionState.Ready;
            item.Update(true, "通道已建立", FeedbackKind.Success);
            Feedback.Status(FeedbackKind.Success, $"已连上 {endpoint.DisplayName} 的 Docker");
            StartEventStream();
            // 容器列表 + 统计采样在后台先跑起来:总览页那几张卡靠它喂,
            // 而用户可能整段时间都不会点开容器页。
            _ = Containers.PrimeAsync(_lifetime.Token);
            if (!ComposeAvailable && CurrentPage == PanelPage.Compose)
            {
                await GoToAsync(PanelPage.Containers).ConfigureAwait(true);
            }
            else
            {
                await RefreshActiveAsync(force: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(true);
            SetFailed(ex, item);
        }
    }

    /// <summary>
    /// 记住这台主机的 socket 路径并重连。
    /// <para>
    /// 路径按<b>主机</b>存,不按会话 id —— 会话 id 每次重连都换,
    /// 而"这台机器的 docker 在哪儿"是这台机器的属性,不该跟着会话一起过期。
    /// </para>
    /// </summary>
    private async Task ApplySocketPathAsync()
    {
        if (SelectedEndpoint is not { } item)
        {
            return;
        }
        string path = SocketPathInput.Trim();
        if (path.Length == 0)
        {
            Feedback.Status(FeedbackKind.Warning, "socket 路径不能为空。");
            return;
        }
        await Settings.SetSocketPathAsync(item.DisplayName, path, _lifetime.Token).ConfigureAwait(true);
        SettingsOpen = false;
        OnPropertyChanged(nameof(SocketPathChanged));
        await ConnectAsync(item).ConfigureAwait(true);
    }

    private void SetNoEndpoint(int connectedSessions)
    {
        State = PanelConnectionState.NoEndpoint;
        ErrorIcon = "Icon.plug";
        ErrorTitle = "选一条已连接的 SSH 会话开始";
        ErrorDetail = "面板不自己发起连接 —— 凭据永远不出宿主核心。选定后会在这条会话上打开一条到 docker.sock 的通道。";
        ErrorHint = connectedSessions switch
        {
            0 => "当前没有已连接的会话 —— 也可以直接管本机 Docker。",
            1 => "当前宿主有 1 条已连接会话。",
            _ => $"当前宿主有 {connectedSessions} 条已连接会话。"
        };
        RecoveryActions.Clear();
        RecoveryActions.Add(new("选择会话…", "Icon.chevron-down", true, () => EndpointMenuOpen = true));
        RecoveryActions.Add(new("管理本机 Docker", "Docker.monitor", false, () =>
            _ = ConnectAsync(Endpoints.FirstOrDefault(e => e.IsLocal))));
    }

    private void SetFailed(Exception ex, EndpointItem item)
    {
        State = PanelConnectionState.Failed;
        RecoveryActions.Clear();
        if (ex is DockerUnreachableException unreachable)
        {
            ErrorHint = unreachable.InnerException?.Message ?? "";
            switch (unreachable.Reason)
            {
                case DockerUnreachableReason.SocketMissing:
                    ErrorIcon = "Docker.circle-x";
                    ErrorTitle = "这台机器上找不到 docker.sock";
                    ErrorDetail = "路径不存在,也没有可用的 DOCKER_HOST。远端可能压根没装 Docker,或者 daemon 没在跑。";
                    RecoveryActions.Add(new("换一个 socket 路径", "Icon.settings", true, () => SettingsOpen = true));
                    item.Update(false, "找不到 docker.sock", FeedbackKind.Error);
                    break;
                case DockerUnreachableReason.PermissionDenied:
                    ErrorIcon = "Docker.lock";
                    ErrorTitle = "当前账号没有 docker.sock 的读写权限";
                    ErrorDetail = "账号不在 docker 组。把账号加进 docker 组,或者换一个有权限的账号 —— 面板不会替你 sudo,那需要一个它拿不到也不该拿的口令。";
                    RecoveryActions.Add(new("怎么加进 docker 组", "Docker.users", false, () =>
                        Feedback.Notify(FeedbackKind.Info, "把账号加进 docker 组",
                            "在远端执行:sudo usermod -aG docker $USER,然后重新登录这条会话。")));
                    item.Update(true, "没有权限", FeedbackKind.Warning);
                    break;
                case DockerUnreachableReason.TunnelUnsupported:
                    ErrorIcon = "Icon.triangle-alert";
                    ErrorTitle = "这个宿主不支持远程隧道";
                    ErrorDetail = "Docker 面板需要 hostMode = inProcess:它交出去的是一条活的字节流,跨进程代理不了。";
                    break;
                case DockerUnreachableReason.SessionUnavailable:
                    ErrorIcon = "Icon.wifi-off";
                    ErrorTitle = "这条 SSH 会话已经断开";
                    ErrorDetail = "重新连上之后再选一次。";
                    item.Update(false, "已断开", FeedbackKind.Error);
                    break;
                default:
                    ErrorIcon = "Icon.circle-alert";
                    ErrorTitle = "连不上 Docker";
                    ErrorDetail = unreachable.Message;
                    break;
            }
        }
        else
        {
            ErrorIcon = "Icon.circle-alert";
            ErrorTitle = "连不上 Docker";
            ErrorDetail = ex.Message;
            ErrorHint = "";
        }
        RecoveryActions.Add(new("重试", "Icon.refresh-cw", RecoveryActions.Count == 0, () => _ = ConnectAsync(item)));
        _context.Log.Warn($"connect to docker failed: {ex.Message}");
    }

    /// <summary>切页。</summary>
    public async Task GoToAsync(PanelPage page)
    {
        if (page == PanelPage.Compose && !ComposeAvailable)
        {
            Feedback.Status(FeedbackKind.Info, "本机端点没有 Compose 页 —— compose 是远端 CLI 上的东西。");
            return;
        }
        CurrentPage = page;
        PageViewModel target = page switch
        {
            PanelPage.Overview => Overview,
            PanelPage.Images => Images,
            PanelPage.Volumes => Volumes,
            PanelPage.Networks => Networks,
            PanelPage.Compose => ComposePage,
            PanelPage.System => SystemPage,
            _ => Containers
        };
        ActivePage = target;
        if (IsReady)
        {
            try
            {
                await target.ActivateAsync(_lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Feedback.ReportError(target.Title, ex);
            }
        }
    }

    /// <summary>刷新当前页。</summary>
    public async Task RefreshActiveAsync(bool force = false)
    {
        if (!IsReady || ActivePage is not { } page)
        {
            return;
        }
        try
        {
            await page.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Feedback.ReportError(page.Title, ex);
        }
    }

    /// <summary>状态栏那串计数:容器数。</summary>
    public void SetContainerCount(int count) => SetCount(ref _countContainers, count);

    /// <summary>状态栏那串计数:镜像数。</summary>
    public void SetImageCount(int count) => SetCount(ref _countImages, count);

    /// <summary>状态栏那串计数:卷数。</summary>
    public void SetVolumeCount(int count) => SetCount(ref _countVolumes, count);

    /// <summary>
    /// 三个数字分开记。
    /// <para>
    /// 早先是一个 <c>SetCounts(a, b, c)</c>,每个调用方只知道自己那一个数,
    /// 另外两个只能去读别的页 —— 而那些页在被打开之前都是 0,
    /// 于是状态栏在用户逛到那一页之前一直显示"0 镜像"。
    /// </para>
    /// </summary>
    private void SetCount(ref int slot, int count)
    {
        if (slot == count)
        {
            return;
        }
        slot = count;
        CountsText = $"{_countContainers} 容器 · {_countImages} 镜像 · {_countVolumes} 卷";
    }

    /// <summary>
    /// 给一条确认请求补上主机信息。
    /// <para>
    /// 调用方不必自己填 —— 漏填一次的代价是一个不写清主机的"确定删除 3 个卷吗",
    /// 而那正是这个面板能犯的最贵的错误。所以这条路是**唯一**打开闸门的路。
    /// </para>
    /// </summary>
    public ConfirmRequest BuildConfirm(ConfirmRequest request) => request with
    {
        HostName = SelectedEndpoint?.DisplayName ?? "(未选择)",
        HostDetail = SelectedEndpoint is { } item
            ? $"· {item.Detail} · {item.Endpoint.SocketPath}"
            : "",
        HostWarning = SelectedEndpoint?.IsLocal == false
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _context.Events.SessionConnected -= OnSessionChanged;
        _context.Events.SessionDisconnected -= OnSessionChanged;
        Confirm.CancelPending();
        Tasks.CancelAll();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await StopEventStreamAsync().ConfigureAwait(false);
        foreach (PageViewModel page in AllPages)
        {
            if (page is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        _lifetime.Dispose();
    }
}
