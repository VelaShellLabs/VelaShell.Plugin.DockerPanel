using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>事件时间线里的一条。</summary>
public sealed class EventItem(DockerEvent source)
{
    /// <summary>时刻。</summary>
    public string Time { get; } = source.At.ToString("HH:mm:ss");

    /// <summary>事件类型(<c>start</c> / <c>die</c> / <c>pull</c>…)。</summary>
    public string Action { get; } = source.Action ?? "";

    /// <summary>对象名。</summary>
    public string Target { get; } = source.DisplayName;

    /// <summary>一句人话。</summary>
    public string Message { get; } = Describe(source);

    /// <summary>语气。</summary>
    public RowTone Tone { get; } = Classify(source);

    private static RowTone Classify(DockerEvent source) => source.Action switch
    {
        "start" or "create" or "health_status: healthy" or "connect" => RowTone.Ok,
        "die" or "oom" or "health_status: unhealthy" => RowTone.Danger,
        "pause" or "restart" => RowTone.Warn,
        "stop" or "kill" or "destroy" or "disconnect" => RowTone.Idle,
        _ => RowTone.Busy
    };

    private static string Describe(DockerEvent source)
    {
        string? exitCode = source.Actor?.Attributes?.GetValueOrDefault("exitCode");
        // 卷与网络的 create 与容器的 create 是同一个词,先按对象类型分流。
        if (source is { Type: "volume", Action: "create" })
        {
            return "本地卷已创建";
        }
        if (source is { Type: "network", Action: "create" })
        {
            return "网络已创建";
        }
        return source.Action switch
        {
            "start" => "容器已启动",
            "stop" => "收到 SIGTERM",
            "kill" => "收到 SIGKILL",
            "die" => exitCode is { Length: > 0 } code ? $"退出码 {code}" : "容器已退出",
            "create" => "容器已创建",
            "destroy" => "容器已移除",
            "pause" => "容器已暂停",
            "unpause" => "容器已恢复",
            "restart" => "容器已重启",
            "oom" => "内存超限,被 OOM 杀掉",
            "pull" => "镜像拉取完成",
            "tag" => "镜像已打标签",
            "untag" => "镜像标签已移除",
            "delete" => "镜像已删除",
            "connect" => "容器已接入网络",
            "disconnect" => "容器已从网络摘除",
            "exec_start" => "exec 会话开始",
            { } action when action.StartsWith("health_status", StringComparison.Ordinal) =>
                action["health_status: ".Length..],
            _ => source.Action ?? ""
        };
    }
}

/// <summary>“需要关注”里的一条。</summary>
/// <param name="Icon">图标。</param>
/// <param name="Tone">语气。</param>
/// <param name="Title">标题。</param>
/// <param name="Detail">小字。</param>
/// <param name="ActionLabel">右侧动作文字。</param>
/// <param name="Action">动作。</param>
public sealed record AttentionItem(string Icon, RowTone Tone, string Title, string Detail, string ActionLabel,
    Action Action);

/// <summary>Top N 排行里的一条。</summary>
public sealed class TopItem(string name, double value, double max, string valueText, bool hot) : ObservableObject
{
    /// <summary>名字。</summary>
    public string Name { get; } = name;

    /// <summary>数值。</summary>
    public double Value { get; } = value;

    /// <summary>相对最大值的比例 0–1(条按这个画,而不是按绝对百分比)。</summary>
    public double Ratio { get; } = max > 0 ? Math.Clamp(value / max, 0, 1) : 0;

    /// <summary>右侧文字。</summary>
    public string ValueText { get; } = valueText;

    /// <summary>要不要转成警示色。</summary>
    public bool Hot { get; } = hot;
}

/// <summary>总览页。</summary>
public sealed class OverviewPageViewModel : PageViewModel
{
    private const int MaxEvents = 60;

    private readonly List<double> _cpuTrend = [];
    private string _runningText = "—";
    private string _runningDetail = "";
    private string _cpuText = "—";
    private string _cpuDetail = "";
    private string _memText = "—";
    private string _memDetail = "";
    private string _reclaimText = "—";
    private string _reclaimDetail = "";
    private long _hostMemory;
    private int _hostCpus;
    private bool _reclaimRequested;

    /// <summary>建总览页。</summary>
    public OverviewPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
        RunContainerCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.Images));
        PullImageCommand = new RelayCommand(_ => Shell.ShowPullDialogAsync(null));
        CleanupCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.System));
        ComposeCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.Compose));
        RefreshReclaimCommand = new RelayCommand(_ => RefreshReclaimAsync(Shell.Lifetime));
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Overview;

    /// <inheritdoc />
    public override string Title => "总览";

    /// <summary>运行中容器。</summary>
    public string RunningText
    {
        get => _runningText;
        private set => SetField(ref _runningText, value);
    }

    /// <summary>运行中容器的小字。</summary>
    public string RunningDetail
    {
        get => _runningDetail;
        private set => SetField(ref _runningDetail, value);
    }

    /// <summary>CPU 总占用。</summary>
    public string CpuText
    {
        get => _cpuText;
        private set => SetField(ref _cpuText, value);
    }

    /// <summary>CPU 小字。</summary>
    public string CpuDetail
    {
        get => _cpuDetail;
        private set => SetField(ref _cpuDetail, value);
    }

    /// <summary>内存。</summary>
    public string MemText
    {
        get => _memText;
        private set => SetField(ref _memText, value);
    }

    /// <summary>内存小字。</summary>
    public string MemDetail
    {
        get => _memDetail;
        private set => SetField(ref _memDetail, value);
    }

    /// <summary>可回收空间。</summary>
    public string ReclaimText
    {
        get => _reclaimText;
        private set => SetField(ref _reclaimText, value);
    }

    /// <summary>可回收小字。</summary>
    public string ReclaimDetail
    {
        get => _reclaimDetail;
        private set => SetField(ref _reclaimDetail, value);
    }

    /// <summary>CPU 趋势采样。</summary>
    public ObservableCollection<double> CpuTrend { get; } = [];

    /// <summary>CPU 占用 Top 5。</summary>
    public ObservableCollection<TopItem> TopCpu { get; } = [];

    /// <summary>实时事件。</summary>
    public ObservableCollection<EventItem> Events { get; } = [];

    /// <summary>需要关注。</summary>
    public ObservableCollection<AttentionItem> Attention { get; } = [];

    /// <summary>有没有要关注的。</summary>
    public bool HasAttention => Attention.Count > 0;

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>运行容器。</summary>
    public RelayCommand RunContainerCommand { get; }

    /// <summary>拉取镜像。</summary>
    public RelayCommand PullImageCommand { get; }

    /// <summary>清理空间。</summary>
    public RelayCommand CleanupCommand { get; }

    /// <summary>去 Compose。</summary>
    public RelayCommand ComposeCommand { get; }

    /// <summary>重算可回收空间。</summary>
    public RelayCommand RefreshReclaimCommand { get; }

    /// <inheritdoc />
    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        Busy = true;
        try
        {
            ContainerSummary[] containers = await client.ListContainersAsync(true, cancellationToken).ConfigureAwait(true);
            SystemInfo info = await client.InfoAsync(cancellationToken).ConfigureAwait(true);
            int running = containers.Count(c => c.State == "running");
            int unhealthy = containers.Count(c => (c.Status ?? "").Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase));
            int failed = containers.Count(c => c.State == "exited" && !(c.Status ?? "").Contains("(0)", StringComparison.Ordinal));
            RunningText = $"{running} / {containers.Length}";
            RunningDetail = $"{unhealthy} 个不健康 · {failed} 个异常退出";
            _hostMemory = info.MemTotal;
            _hostCpus = info.NCPU;
            if (_memText == "—")
            {
                MemDetail = $"共 {Humanize.Bytes(info.MemTotal)} · {info.NCPU} 核";
            }
            BuildAttention(containers);
            Shell.SetContainerCount(containers.Length);
            Shell.SetImageCount(info.Images);
            LoadedOnce = true;
            OnPropertyChanged(nameof(HasAttention));
        }
        finally
        {
            Busy = false;
        }
        // 第一次进来时在后台算一次可回收空间 —— 它太慢,不能挂在刷新的主路径上。
        if (!_reclaimRequested)
        {
            _ = RefreshReclaimAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        Events.Clear();
        Attention.Clear();
        TopCpu.Clear();
        CpuTrend.Clear();
        _cpuTrend.Clear();
        LoadedOnce = false;
        RunningText = "—";
        CpuText = "—";
        MemText = "—";
        MemDetail = "";
        ReclaimText = "—";
        ReclaimDetail = "";
        _hostMemory = 0;
        _hostCpus = 0;
        _reclaimRequested = false;
        OnPropertyChanged(nameof(HasAttention));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type is "container" or "image";

    /// <summary>收下一条事件,进时间线。</summary>
    public void AcceptEvent(DockerEvent dockerEvent)
    {
        Events.Insert(0, new(dockerEvent));
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    /// <summary>
    /// 用容器页那份已经采到的统计更新 Top 5 与趋势 ——
    /// 不再单独发一轮请求。
    /// </summary>
    public void AcceptStatsSnapshot(IReadOnlyList<ContainerRow> rows)
    {
        List<ContainerRow> running = [.. rows.Where(r => r.IsRunning && r.CpuPercent > 0)];
        double total = running.Sum(r => r.CpuPercent);
        CpuText = Humanize.Percent(total);
        CpuDetail = running.Count > 0 ? $"{running.Count} 个容器在用 CPU" : "没有容器在用 CPU";
        // 内存卡走的是同一批采样:容器占用之和 / 宿主总量,
        // 单独再问一次 daemon 只会得到同样的数字。
        long usedMemory = rows.Where(r => r.IsRunning).Sum(r => r.MemoryBytes);
        MemText = usedMemory > 0 ? Humanize.Bytes(usedMemory) : "0 B";
        MemDetail = _hostMemory > 0
            ? $"容器占用 · 宿主共 {Humanize.Bytes(_hostMemory)}{(_hostCpus > 0 ? $" · {_hostCpus} 核" : "")}"
            : "容器占用";
        _cpuTrend.Add(total);
        while (_cpuTrend.Count > 48)
        {
            _cpuTrend.RemoveAt(0);
        }
        CpuTrend.Clear();
        foreach (double sample in _cpuTrend)
        {
            CpuTrend.Add(sample);
        }
        double max = running.Count > 0 ? running.Max(r => r.CpuPercent) : 0;
        TopCpu.Clear();
        foreach (ContainerRow row in running.OrderByDescending(r => r.CpuPercent).Take(5))
        {
            TopCpu.Add(new(row.Name, row.CpuPercent, max, Humanize.Percent(row.CpuPercent), row.CpuHot));
        }
    }

    /// <summary>
    /// 用户在设置里关掉了实时统计。这两张卡不该继续显示一个停在过去某一刻的数字 ——
    /// 说清"是被关掉了"比留一个看不出新鲜度的旧值诚实。
    /// </summary>
    public void SetStatsDisabled()
    {
        CpuText = "—";
        CpuDetail = "实时统计已在设置里关闭";
        MemText = "—";
        MemDetail = _hostMemory > 0 ? $"宿主共 {Humanize.Bytes(_hostMemory)} · {_hostCpus} 核" : "";
        TopCpu.Clear();
        CpuTrend.Clear();
        _cpuTrend.Clear();
    }

    /// <summary>更新"可回收空间"那张卡(系统页算完之后顺手喂进来)。</summary>
    public void AcceptReclaim(ReclaimBreakdown reclaim)
    {
        _reclaimRequested = true;
        ReclaimText = Humanize.Bytes(reclaim.Total);
        ReclaimDetail = reclaim.Describe();
    }

    /// <summary>
    /// 自己去算一次可回收空间。
    /// <para>
    /// <c>system df</c> 会让 daemon 把每个卷 <c>du</c> 一遍,在卷多的机器上要好几秒 ——
    /// 所以它<b>不</b>跟着总览的每次刷新跑,只在第一次进来时算一遍,
    /// 之后靠系统页的刷新或卡片上的重算按钮更新。
    /// </para>
    /// </summary>
    public async Task RefreshReclaimAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        _reclaimRequested = true;
        ReclaimText = "…";
        ReclaimDetail = "正在统计";
        try
        {
            DiskUsage usage = await client.DiskUsageAsync(cancellationToken).ConfigureAwait(true);
            AcceptReclaim(DiskMath.Reclaimable(usage));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReclaimText = "—";
            ReclaimDetail = "统计失败";
            Shell.Context.Log.Warn($"overview: system df failed: {ex.Message}");
        }
    }

    private void BuildAttention(IReadOnlyList<ContainerSummary> containers)
    {
        Attention.Clear();
        foreach (ContainerSummary container in containers)
        {
            string status = container.Status ?? "";
            if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase))
            {
                Attention.Add(new("Icon.triangle-alert", RowTone.Danger,
                    $"{container.Name} 健康检查失败",
                    $"{container.Image} · {status}",
                    "查看日志",
                    () => OpenContainer(container.Id)));
            }
            else if (container.State == "exited" && !status.Contains("(0)", StringComparison.Ordinal))
            {
                Attention.Add(new("Docker.circle-x", RowTone.Danger,
                    $"{container.Name} 异常退出",
                    $"{container.Image} · {status}",
                    "查看日志",
                    () => OpenContainer(container.Id)));
            }
            else if (container.State == "paused")
            {
                Attention.Add(new("Icon.pause", RowTone.Warn,
                    $"{container.Name} 处于暂停态",
                    "进程还在,但不会处理任何请求。",
                    "去恢复",
                    () => OpenContainer(container.Id)));
            }
        }
        if (Shell.Volumes.UnusedCount > 0)
        {
            Attention.Add(new("Docker.database", RowTone.Warn,
                $"{Shell.Volumes.UnusedCount} 个卷没有容器在用",
                "它们可能是刚 down 掉的项目留下的 —— 也可能就是垃圾。",
                "去看看",
                () => _ = Shell.GoToAsync(PanelPage.Volumes)));
        }
    }

    private void OpenContainer(string id)
    {
        _ = Shell.GoToAsync(PanelPage.Containers).ContinueWith(_ => Ui.Post(() =>
        {
            if (Shell.Containers.View.FirstOrDefault(r => r.Id == id) is { } row)
            {
                Shell.Containers.OpenDetailCommand.Execute(row);
            }
        }), TaskScheduler.Default);
    }
}
