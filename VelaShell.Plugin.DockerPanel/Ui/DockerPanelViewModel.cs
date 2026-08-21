using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Globalization;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>面板的六个页签。</summary>
public enum DockerTab
{
    /// <summary>容器。</summary>
    Containers,

    /// <summary>镜像。</summary>
    Images,

    /// <summary>卷。</summary>
    Volumes,

    /// <summary>网络。</summary>
    Networks,

    /// <summary>Compose 项目。</summary>
    Compose,

    /// <summary>系统信息与空间回收。</summary>
    System
}

/// <summary>底部抽屉的内容。</summary>
public enum DrawerTab
{
    /// <summary>inspect 的 JSON。</summary>
    Details,

    /// <summary>容器日志。</summary>
    Logs,

    /// <summary>容器内进程。</summary>
    Top,

    /// <summary>容器文件变更。</summary>
    Diff,

    /// <summary>容器端口。</summary>
    Ports,

    /// <summary>镜像构建历史。</summary>
    History,

    /// <summary>compose 服务列表。</summary>
    Services,

    /// <summary>compose 展开后的配置。</summary>
    Config,

    /// <summary>compose 文件正文(可编辑)。</summary>
    File,

    /// <summary>面板发出的每一条远端命令。</summary>
    Output
}

/// <summary>
/// Docker 面板的视图模型。按域拆成几个 partial 文件;这一份管**骨架**:
/// 会话绑定、引擎探测、设置、自动刷新、页签、状态栏,以及所有页签共用的那几个动作。
/// </summary>
public sealed partial class DockerPanelViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPluginContext _context;
    private readonly Loc _loc;
    private readonly CancellationTokenSource _lifetime;

    /// <summary>
    /// 远端操作的串行闸。
    /// <para>
    /// 同一条 SSH 会话上并发开 exec 通道是可以的,但**面板不该这么做**:自动刷新与用户按下的
    /// "停止容器"撞在一起时,刷新会读到一个正在变的中间态,然后把用户刚点亮的行刷没了。
    /// 一次一条,顺带也是对远端友好(§9)。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>自动刷新被关掉时,循环挂在这个信号上等 —— 而不是每秒醒来看一眼(§9 不轮询)。</summary>
    private readonly SemaphoreSlim _autoRefreshSignal = new(0, 1);

    private readonly Action<SessionInfo> _onSessionConnected;
    private readonly Action<SessionInfo> _onSessionDisconnected;

    private Task? _refreshLoop;
    private DockerApi? _api;
    private bool _disposed;
    private bool _settingsLoaded;

    private SessionOption? _selectedSession;
    private string _engineText = string.Empty;
    private string _engineDetail = string.Empty;
    private bool _isEngineReady;
    private string _countsText = string.Empty;
    private bool _useSudo;
    private string _dockerHostValue = string.Empty;
    private string _dockerContextValue = string.Empty;
    private bool _isSettingsOpen;
    private int _autoRefreshSeconds;
    private DockerTab _activeTab = DockerTab.Containers;
    private string _filter = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;

    /// <summary>构造。</summary>
    /// <param name="context">插件上下文。</param>
    /// <param name="loc">文案表。</param>
    public DockerPanelViewModel(IPluginContext context, Loc loc)
    {
        _context = context;
        _loc = loc;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown);
        _status = loc["Status_Ready"];
        _engineText = loc["Engine_Probing"];
        // 最短 5 秒:§9 的纪律,也是常识 —— 每条刷新都是一次 SSH exec,
        // 一秒一次会把跨洋链路刷成一条常亮的进度条。
        RefreshChoices =
        [
            new("0", loc["Header_AutoOff"]),
            new("5", loc.Format("Header_Seconds", 5)),
            new("10", loc.Format("Header_Seconds", 10)),
            new("30", loc.Format("Header_Seconds", 30)),
            new("60", loc.Format("Header_Seconds", 60))
        ];

        RefreshCommand = new(() => RefreshActiveAsync(false));
        ToggleSettingsCommand = new(() =>
        {
            IsSettingsOpen = !IsSettingsOpen;
            return Task.CompletedTask;
        });
        ApplySettingsCommand = new(ApplySettingsAsync);
        SelectTabCommand = new(SelectTabAsync);
        SelectDrawerCommand = new(SelectDrawerAsync);
        HideDrawerCommand = new(() =>
        {
            IsDrawerOpen = false;
            return Task.CompletedTask;
        });
        CopyDrawerCommand = new(() => CopyAsync(DrawerText));
        ClearDrawerCommand = new(() =>
        {
            if (DrawerContent is DrawerTab.Output)
            {
                _commandLog.Clear();
                DrawerText = string.Empty;
            }
            return Task.CompletedTask;
        });
        ReloadDrawerCommand = new(() => LoadDrawerAsync(true));
        SaveDrawerCommand = new(SaveDrawerAsync, () => IsDrawerEditable);

        BuildContainerCommands();
        BuildImageCommands();
        BuildVolumeCommands();
        BuildNetworkCommands();
        BuildComposeCommands();
        BuildSystemCommands();

        _onSessionConnected = _ => QueueSessionRefresh();
        _onSessionDisconnected = _ => QueueSessionRefresh();
        context.Events.SessionConnected += _onSessionConnected;
        context.Events.SessionDisconnected += _onSessionDisconnected;
    }

    /// <summary>面板内的确认闸门。</summary>
    public Confirmation Confirm { get; } = new();

    /// <summary>面板内的通用表单。</summary>
    public PanelForm Form { get; } = new();

    /// <summary>可选的 SSH 会话。</summary>
    public ObservableCollection<SessionOption> Sessions { get; } = [];

    /// <summary>自动刷新间隔的可选项。</summary>
    public IReadOnlyList<FormChoice> RefreshChoices { get; }

    /// <summary>刷新当前页签。</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>展开/收起设置行。</summary>
    public AsyncCommand ToggleSettingsCommand { get; }

    /// <summary>应用设置(sudo / DOCKER_HOST)并重新探测。</summary>
    public AsyncCommand ApplySettingsCommand { get; }

    /// <summary>切页签。</summary>
    public AsyncCommand<string> SelectTabCommand { get; }

    /// <summary>切抽屉内容。</summary>
    public AsyncCommand<string> SelectDrawerCommand { get; }

    /// <summary>收起抽屉。</summary>
    public AsyncCommand HideDrawerCommand { get; }

    /// <summary>复制抽屉里的全部文本。</summary>
    public AsyncCommand CopyDrawerCommand { get; }

    /// <summary>清空(仅执行记录)。</summary>
    public AsyncCommand ClearDrawerCommand { get; }

    /// <summary>重新读取抽屉内容。</summary>
    public AsyncCommand ReloadDrawerCommand { get; }

    /// <summary>保存抽屉里的内容(仅 compose 文件编辑)。</summary>
    public AsyncCommand SaveDrawerCommand { get; }

    /// <summary>当前选中的会话。</summary>
    public SessionOption? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value))
            {
                return;
            }
            RaisePropertyChanged(nameof(HasSession));
            _ = RebindSessionAsync();
        }
    }

    /// <summary>有选中的会话。</summary>
    public bool HasSession => _selectedSession is not null;

    /// <summary>引擎状态那一格的文字(<c>docker 27.3.1</c> 或一句问题描述)。</summary>
    public string EngineText
    {
        get => _engineText;
        private set => SetProperty(ref _engineText, value);
    }

    /// <summary>引擎状态的补充说明;为空则不占位置。</summary>
    public string EngineDetail
    {
        get => _engineDetail;
        private set
        {
            SetProperty(ref _engineDetail, value);
            RaisePropertyChanged(nameof(HasEngineDetail));
        }
    }

    /// <summary>有补充说明。</summary>
    public bool HasEngineDetail => EngineDetail.Length > 0;

    /// <summary>docker 可用。界面据此把一整排动作按钮灰掉,而不是让每一次点击都撞一条错误。</summary>
    public bool IsEngineReady
    {
        get => _isEngineReady;
        private set => SetProperty(ref _isEngineReady, value);
    }

    /// <summary>头部那一行计数。</summary>
    public string CountsText
    {
        get => _countsText;
        private set => SetProperty(ref _countsText, value);
    }

    /// <summary>是否以 sudo 执行。</summary>
    public bool UseSudo
    {
        get => _useSudo;
        set => SetProperty(ref _useSudo, value);
    }

    /// <summary>自定义 DOCKER_HOST。</summary>
    public string DockerHostValue
    {
        get => _dockerHostValue;
        set => SetProperty(ref _dockerHostValue, value);
    }

    /// <summary>自定义 DOCKER_CONTEXT。</summary>
    public string DockerContextValue
    {
        get => _dockerContextValue;
        set => SetProperty(ref _dockerContextValue, value);
    }

    /// <summary>设置行是否展开。</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    /// <summary>自动刷新间隔(秒);<c>0</c> 表示关。</summary>
    public int AutoRefreshSeconds
    {
        get => _autoRefreshSeconds;
        set
        {
            if (!SetProperty(ref _autoRefreshSeconds, value))
            {
                return;
            }
            if (value > 0 && _autoRefreshSignal.CurrentCount == 0)
            {
                // 从"关"切到"开":把挂着的循环叫醒。CurrentCount 的检查是尽力而为的 ——
                // 真撞上并发 Release 也只是多放一次行,循环下一轮照常。
                try
                {
                    _autoRefreshSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // 已经是"开"了,无事可做。
                }
            }
            _ = SaveSettingAsync("autoRefresh", value);
        }
    }

    /// <summary>自动刷新下拉的选中项。</summary>
    public FormChoice? SelectedRefreshChoice
    {
        get => RefreshChoices.FirstOrDefault(c => c.Value == AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture))
               ?? RefreshChoices[0];
        set
        {
            if (value is not null && int.TryParse(value.Value, out var seconds))
            {
                AutoRefreshSeconds = seconds;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>当前页签。</summary>
    public DockerTab ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (!SetProperty(ref _activeTab, value))
            {
                return;
            }
            RaisePropertyChanged(nameof(IsContainersTab));
            RaisePropertyChanged(nameof(IsImagesTab));
            RaisePropertyChanged(nameof(IsVolumesTab));
            RaisePropertyChanged(nameof(IsNetworksTab));
            RaisePropertyChanged(nameof(IsComposeTab));
            RaisePropertyChanged(nameof(IsSystemTab));
            RaisePropertyChanged(nameof(ShowsList));
        }
    }

    /// <summary>在容器页。</summary>
    public bool IsContainersTab => ActiveTab is DockerTab.Containers;

    /// <summary>在镜像页。</summary>
    public bool IsImagesTab => ActiveTab is DockerTab.Images;

    /// <summary>在卷页。</summary>
    public bool IsVolumesTab => ActiveTab is DockerTab.Volumes;

    /// <summary>在网络页。</summary>
    public bool IsNetworksTab => ActiveTab is DockerTab.Networks;

    /// <summary>在 Compose 页。</summary>
    public bool IsComposeTab => ActiveTab is DockerTab.Compose;

    /// <summary>在系统页。</summary>
    public bool IsSystemTab => ActiveTab is DockerTab.System;

    /// <summary>当前页签是列表形态(系统页不是)—— 过滤框与抽屉只对列表页有意义。</summary>
    public bool ShowsList => ActiveTab is not DockerTab.System;

    /// <summary>过滤串(对当前页签的列表生效)。</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>状态栏文字。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>是否有远端操作在飞(状态栏据此转圈)。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>文案表(视图里直接绑标签用)。</summary>
    public Loc Text => _loc;

    /// <summary>首次装载:列会话、挑一条、探测。</summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task InitializeAsync()
    {
        await LoadGlobalSettingsAsync().ConfigureAwait(true);
        await ReloadSessionsAsync().ConfigureAwait(true);
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_lifetime.Token));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // 先收掉两条长驻流:它们各占一个 SSH 通道,不取消就要挂到宿主的死线才散。
        StopEventStream();
        StopLogStream();
        _context.Events.SessionConnected -= _onSessionConnected;
        _context.Events.SessionDisconnected -= _onSessionDisconnected;
        Confirm.Dismiss();
        Form.Dismiss();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_refreshLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停机路径上的取消是预期结果。
            }
        }
        _lifetime.Dispose();
        _gate.Dispose();
        _autoRefreshSignal.Dispose();
    }

    /// <summary>重新枚举会话并尽量保住当前选中的那条。</summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task ReloadSessionsAsync()
    {
        IReadOnlyList<SessionInfo> sessions;
        try
        {
            sessions = await _context.Sessions.ListAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var keep = SelectedSession?.SessionId;
        Sessions.Clear();
        foreach (var session in sessions.Where(static s => s.State is SessionState.Connected))
        {
            var user = session.Username.Length > 0 ? $"{session.Username}@" : string.Empty;
            Sessions.Add(new(session.SessionId, $"{user}{session.Host}:{session.Port}", session.Host));
        }
        var next = Sessions.FirstOrDefault(s => s.SessionId == keep) ?? Sessions.FirstOrDefault();
        if (!ReferenceEquals(next, SelectedSession))
        {
            SelectedSession = next;
        }
        else if (next is null)
        {
            SetEngineUnavailable();
        }
    }

    private void QueueSessionRefresh() =>
        // 宿主事件在非 UI 线程触发且必须立刻返回。活儿本身要在 UI 线程上做 ——
        // Sessions 是绑定着的 ObservableCollection,在别的线程上改它会当场炸掉绑定。
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ReloadSessionsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _context.Log.Warn("Refreshing the session list failed.", ex);
            }
        });

    private async Task RebindSessionAsync()
    {
        if (SelectedSession is not { } session)
        {
            _api = null;
            SetEngineUnavailable();
            return;
        }
        await LoadHostSettingsAsync(session.Host).ConfigureAwait(true);
        DockerEngine engine = new(_context, session.SessionId)
        {
            UseSudo = UseSudo,
            DockerHost = DockerHostValue.Trim(),
            DockerContext = DockerContextValue.Trim(),
            CommandObserved = OnCommandObserved
        };
        _api = new(engine);
        ClearAll();
        await ProbeAsync().ConfigureAwait(true);
    }

    private async Task ApplySettingsAsync()
    {
        if (SelectedSession is not { } session)
        {
            return;
        }
        await SaveSettingAsync($"sudo:{session.Host}", UseSudo).ConfigureAwait(true);
        await SaveSettingAsync($"dockerHost:{session.Host}", DockerHostValue.Trim()).ConfigureAwait(true);
        await SaveSettingAsync($"dockerContext:{session.Host}", DockerContextValue.Trim()).ConfigureAwait(true);
        if (_api is { } api)
        {
            api.Engine.UseSudo = UseSudo;
            api.Engine.DockerHost = DockerHostValue.Trim();
            api.Engine.DockerContext = DockerContextValue.Trim();
        }
        IsSettingsOpen = false;
        await ProbeAsync().ConfigureAwait(true);
    }

    private async Task ProbeAsync()
    {
        if (_api is not { } api)
        {
            SetEngineUnavailable();
            return;
        }
        EngineText = _loc["Engine_Probing"];
        EngineDetail = string.Empty;
        IsEngineReady = false;
        var probe = await GuardAsync(token => api.Engine.ProbeAsync(token)).ConfigureAwait(true);
        if (probe.IsUsable)
        {
            IsEngineReady = true;
            EngineText = _loc.Format("Engine_Ready", probe.ServerVersion);
            EngineDetail = probe.HasCompose
                ? _loc.Format("Engine_Compose", probe.ComposeVersion)
                : _loc["Engine_ComposeMissing"];
            // 接上 daemon 的事件流:此后容器起停、镜像拉取都由事件驱动刷新,
            // 定时器只是接不上事件时的退路(§9:能用事件就不用定时器)。
            StartEventStream();
            await RefreshActiveAsync(true).ConfigureAwait(true);
            return;
        }
        StopEventStream();
        IsEngineReady = false;
        EngineText = probe.Diagnostic switch
        {
            "missing" => _loc["Engine_Missing"],
            "daemon" => _loc["Engine_Daemon"],
            "denied" => _loc["Engine_Denied"],
            "sudo-password" => _loc["Engine_SudoPassword"],
            _ => _loc.Format("Engine_Other", probe.Diagnostic)
        };
        EngineDetail = string.Empty;
        CountsText = string.Empty;
    }

    private void SetEngineUnavailable()
    {
        StopEventStream();
        StopLogStream();
        IsEngineReady = false;
        EngineText = _loc["Engine_NoSession"];
        EngineDetail = string.Empty;
        CountsText = string.Empty;
        ClearAll();
    }

    private async Task SelectTabAsync(string tab)
    {
        if (!Enum.TryParse(tab, out DockerTab parsed))
        {
            return;
        }
        ActiveTab = parsed;
        ResetDrawerForTab();
        await RefreshActiveAsync(true).ConfigureAwait(true);
    }

    /// <summary>刷新当前页签的数据。</summary>
    /// <param name="silent">静默刷新(自动刷新与切页签用)不改状态栏文字。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task RefreshActiveAsync(bool silent)
    {
        if (_api is null || !IsEngineReady)
        {
            if (!silent)
            {
                Status = _loc[HasSession ? "Status_EngineUnavailable" : "Status_NoSession"];
            }
            return;
        }
        switch (ActiveTab)
        {
            case DockerTab.Containers:
                await LoadContainersAsync().ConfigureAwait(true);
                break;
            case DockerTab.Images:
                await LoadImagesAsync().ConfigureAwait(true);
                break;
            case DockerTab.Volumes:
                await LoadVolumesAsync().ConfigureAwait(true);
                break;
            case DockerTab.Networks:
                await LoadNetworksAsync().ConfigureAwait(true);
                break;
            case DockerTab.Compose:
                await LoadComposeAsync().ConfigureAwait(true);
                break;
            case DockerTab.System:
                await LoadSystemAsync().ConfigureAwait(true);
                break;
        }
        if (!silent)
        {
            Status = _loc.Format("Status_Refreshed", DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var seconds = AutoRefreshSeconds;
            try
            {
                if (seconds <= 0)
                {
                    // 关掉自动刷新时不留任何定时器:挂在信号上,直到用户重新打开。
                    await _autoRefreshSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, seconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cancellationToken.IsCancellationRequested || _api is null || !IsEngineReady)
            {
                continue;
            }
            // 用户正对着确认框或表单时不刷新:列表在脚下变掉会把"我要删的是这一行"变成一句谎话。
            if (Confirm.IsOpen || Form.IsOpen)
            {
                continue;
            }
            try
            {
                // 刷新会改绑定着的集合,整段必须在 UI 线程上跑。等待远端的那几百毫秒
                // 是 await 出去的,不会冻界面 —— 冻界面的是同步阻塞,不是在 UI 线程上 await。
                // 日志不在这里管了:跟随是一条真正的 `docker logs -f` 流,自己会推。
                await Dispatcher.UIThread.InvokeAsync(() => RefreshActiveAsync(true)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _context.Log.Warn("Auto refresh failed.", ex);
            }
        }
    }

    /// <summary>
    /// 把一次远端调用串起来,并在状态栏上转圈。
    /// 所有远端调用都该从这里走 —— 它保证同一时刻只有一条命令在飞,
    /// 也保证 <see cref="IsBusy" /> 一定会被复位(哪怕命令体抛了)。
    /// </summary>
    /// <typeparam name="T">返回类型。</typeparam>
    /// <param name="action">要执行的远端调用。</param>
    /// <returns>调用结果。</returns>
    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        // 面板关掉之后仍可能有几个"即发即忘"的加载在半路上(抽屉、选中项联动)。
        // 那时闸门已经释放,再去 WaitAsync 就是一个没人接的 ObjectDisposedException。
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        IsBusy = true;
        try
        {
            return await action(_lifetime.Token).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    private async Task GuardAsync(Func<CancellationToken, Task> action) =>
        await GuardAsync<bool>(async token =>
        {
            await action(token).ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);

    /// <summary>把一批操作的结果汇成状态栏上的一句话。</summary>
    /// <param name="label">操作名。</param>
    /// <param name="outcomes">逐个目标的结果。</param>
    private void ReportBatch(string label, IReadOnlyList<BatchOutcome> outcomes)
    {
        var ok = outcomes.Count(static o => o.IsSuccess);
        var failed = outcomes.Count - ok;
        if (failed == 0)
        {
            Status = _loc.Format("Status_BatchOk", label, ok);
            return;
        }
        // 只报第一条失败原因:十个容器同一个原因失败时,把十条一样的话拼进状态栏毫无用处。
        var reason = outcomes.First(static o => !o.IsSuccess).Output;
        Status = _loc.Format("Status_Batch", label, ok, failed, FirstLine(reason));
    }

    private void ReportResult(string label, ExecResult result)
    {
        Status = result.IsSuccess
            ? _loc.Format("Status_Done", label)
            : _loc.Format("Status_Failed", label, FirstLine(result.FailureText));
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length > 240 ? trimmed[..240] + "…" : trimmed;
            }
        }
        return text.Trim();
    }

    /// <summary>复制到系统剪贴板。</summary>
    /// <param name="text">文本。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            await _context.Clipboard.SetTextAsync(text, _lifetime.Token).ConfigureAwait(true);
            Status = _loc["Status_Copied"];
        }
        catch (Exception ex)
        {
            _context.Log.Warn("Copying to the clipboard failed.", ex);
        }
    }

    /// <summary>
    /// 把一条命令敲进这条会话的终端标签。
    /// <para>
    /// 宿主会为此弹一次授权(仅本次 / 本次运行 / 始终 / 拒绝)。被拒绝就体面地说一句然后收手 ——
    /// 反复问是让用户把"始终拒绝"点下去的最快办法。
    /// </para>
    /// </summary>
    /// <param name="command">命令(不含换行)。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task SendToTerminalAsync(string command)
    {
        if (SelectedSession is not { } session)
        {
            Status = _loc["Status_NoSession"];
            return;
        }
        try
        {
            await _context.Terminal.WriteAsync(session.SessionId, command + "\n", _lifetime.Token).ConfigureAwait(true);
            Status = _loc["Status_TerminalSent"];
        }
        catch (PluginPermissionDeniedException)
        {
            Status = _loc["Status_TerminalDenied"];
        }
        catch (Exception ex)
        {
            Status = _loc.Format("Status_Failed", _loc["Container_Terminal"], ex.Message);
        }
    }

    /// <summary>
    /// 换会话时把一切清空。
    /// <para>
    /// 尤其是选中集合与手工打开的 compose 路径:上一台机器上选中的容器 id、
    /// 手敲的 <c>/srv/app/docker-compose.yml</c>,在新机器上要么不存在,要么**是别的东西**。
    /// 留着它们就意味着"切了台机器,按下删除,删了另一台上同名的东西"。
    /// </para>
    /// </summary>
    private void ClearAll()
    {
        Containers.Clear();
        Images.Clear();
        Volumes.Clear();
        Networks.Clear();
        ComposeProjects.Clear();
        _allContainers = [];
        _allImages = [];
        _allVolumes = [];
        _allNetworks = [];
        _allComposeProjects = [];
        _manualProjects.Clear();
        _selectedContainers = [];
        _selectedImages = [];
        _selectedVolumes = [];
        _selectedNetworks = [];
        _selectedCompose = null;
        RaisePropertyChanged(nameof(PrimaryContainer));
        RaisePropertyChanged(nameof(PrimaryImage));
        RaisePropertyChanged(nameof(PrimaryVolume));
        RaisePropertyChanged(nameof(PrimaryNetwork));
        RaisePropertyChanged(nameof(SelectedCompose));
        RaisePropertyChanged(nameof(SelectionSummary));
        RaiseContainerCommandStates();
        SystemVersionText = string.Empty;
        SystemInfoText = string.Empty;
        SystemDiskText = string.Empty;
        DrawerText = string.Empty;
        DrawerTitle = string.Empty;
        CountsText = string.Empty;
    }

    private void ApplyFilter()
    {
        switch (ActiveTab)
        {
            case DockerTab.Containers:
                PublishContainers();
                break;
            case DockerTab.Images:
                PublishImages();
                break;
            case DockerTab.Volumes:
                PublishVolumes();
                break;
            case DockerTab.Networks:
                PublishNetworks();
                break;
            case DockerTab.Compose:
                PublishCompose();
                break;
        }
    }

    /// <summary>过滤判定:大小写不敏感的子串匹配,任何一段命中即可。</summary>
    /// <param name="parts">这一行里可搜的几段文字。</param>
    /// <returns>是否留下。</returns>
    private bool Matches(params string?[] parts)
    {
        var needle = Filter.Trim();
        if (needle.Length == 0)
        {
            return true;
        }
        foreach (var part in parts)
        {
            if (part is not null && part.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task LoadGlobalSettingsAsync()
    {
        try
        {
            _autoRefreshSeconds = await _context.Storage.GetAsync<int>("autoRefresh", _lifetime.Token).ConfigureAwait(true);
            ShowAllContainers = !await _context.Storage.GetAsync<bool>("containersRunningOnly", _lifetime.Token).ConfigureAwait(true);
            ShowStats = !await _context.Storage.GetAsync<bool>("statsOff", _lifetime.Token).ConfigureAwait(true);
            ShowAllImages = await _context.Storage.GetAsync<bool>("showAllImages", _lifetime.Token).ConfigureAwait(true);
            ShowContainerSize = await _context.Storage.GetAsync<bool>("showContainerSize", _lifetime.Token).ConfigureAwait(true);
            var tail = await _context.Storage.GetAsync<int>("logTail", _lifetime.Token).ConfigureAwait(true);
            if (tail > 0)
            {
                LogTail = tail;
            }
        }
        catch (Exception ex)
        {
            _context.Log.Warn("Reading plugin settings failed; defaults are used.", ex);
        }
        RaisePropertyChanged(nameof(AutoRefreshSeconds));
        RaisePropertyChanged(nameof(SelectedRefreshChoice));
        _settingsLoaded = true;
    }

    private async Task LoadHostSettingsAsync(string host)
    {
        try
        {
            UseSudo = await _context.Storage.GetAsync<bool>($"sudo:{host}", _lifetime.Token).ConfigureAwait(true);
            DockerHostValue = await _context.Storage.GetAsync<string>($"dockerHost:{host}", _lifetime.Token).ConfigureAwait(true) ?? string.Empty;
            DockerContextValue = await _context.Storage.GetAsync<string>($"dockerContext:{host}", _lifetime.Token).ConfigureAwait(true) ?? string.Empty;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Reading per-host settings for {host} failed.", ex);
        }
    }

    private async Task SaveSettingAsync<T>(string key, T value)
    {
        if (!_settingsLoaded)
        {
            // 初始化阶段的赋值不该反过来把刚读出来的值写回去。
            return;
        }
        try
        {
            await _context.Storage.SetAsync(key, value, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Persisting setting '{key}' failed.", ex);
        }
    }
}
