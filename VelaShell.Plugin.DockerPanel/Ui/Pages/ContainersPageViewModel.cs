using System.Collections.ObjectModel;
using Avalonia.Controls;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>容器页的筛选档。</summary>
public enum ContainerFilter
{
    /// <summary>全部。</summary>
    All,

    /// <summary>运行中。</summary>
    Running,

    /// <summary>已停止。</summary>
    Stopped,

    /// <summary>异常(不健康 / 非零退出)。</summary>
    Problem
}

/// <summary>
/// 筛选弹层里的一项 compose 项目(设计稿 19 号板)。
/// <para>
/// 名字为空串代表「不属于任何项目」—— 那也是一档真实的筛选,而不是"没选"。
/// </para>
/// </summary>
/// <param name="Name">项目名;空串表示不属于任何项目。</param>
/// <param name="Label">显示文字。</param>
/// <param name="Count">这一档有几个容器。</param>
public readonly record struct ProjectFilterItem(string Name, string Label, int Count);

/// <summary>容器页。</summary>
public sealed class ContainersPageViewModel : PageViewModel, IAsyncDisposable
{
    private readonly List<ContainerRow> _all = [];
    private readonly StatsSampler _sampler;
    private ContainerFilter _filter = ContainerFilter.All;
    private string? _project;
    private string _search = "";
    private int _selectedCount;
    private ContainerDetailViewModel? _detail;
    private LogsViewModel? _mergedLogs;
    private string _logSourceSearch = "";
    private double _drawerWidth = 440;
    private bool _detailMaximized;

    /// <summary>抽屉再窄就摆不下头里那一行了。</summary>
    private const double MinDrawerWidth = 360;

    /// <summary>建容器页。</summary>
    public ContainersPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        _sampler = new(() => Client);
        SetFilterCommand = new RelayCommand(p =>
        {
            if (p is ContainerFilter filter)
            {
                Filter = filter;
            }
        });
        OpenDetailCommand = new RelayCommand(p => p is ContainerRow row ? OpenDetailAsync(row) : Task.CompletedTask);
        CloseDetailCommand = new RelayCommand(_ => CloseDetail());
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        StartCommand = new RelayCommand(p => BatchAsync("启动", Targets(p),
            (c, id, ct) => c.StartContainerAsync(id, ct)));
        StopCommand = new RelayCommand(p => BatchAsync("停止", Targets(p),
            (c, id, ct) => c.StopContainerAsync(id, 10, ct)));
        RestartCommand = new RelayCommand(p => BatchAsync("重启", Targets(p),
            (c, id, ct) => c.RestartContainerAsync(id, 10, ct)));
        PauseCommand = new RelayCommand(p => BatchAsync("暂停", Targets(p),
            (c, id, ct) => c.PauseContainerAsync(id, ct)));
        UnpauseCommand = new RelayCommand(p => BatchAsync("恢复", Targets(p),
            (c, id, ct) => c.UnpauseContainerAsync(id, ct)));
        KillCommand = new RelayCommand(p => KillAsync(Targets(p)));
        RemoveCommand = new RelayCommand(p => RemoveAsync(Targets(p)));
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Containers;

    /// <inheritdoc />
    public override string Title => "容器";

    /// <summary>过滤后的行(界面绑这个)。</summary>
    public KeyedCollection<ContainerRow> View { get; } = new(r => r.Id);

    /// <summary>当前筛选档。</summary>
    public ContainerFilter Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value))
            {
                OnPropertiesChanged(nameof(IsAll), nameof(IsRunningFilter), nameof(IsStoppedFilter), nameof(IsProblemFilter));
                ApplyView();
            }
        }
    }

    /// <summary>筛选:全部。</summary>
    public bool IsAll => Filter == ContainerFilter.All;

    /// <summary>筛选:运行中。</summary>
    public bool IsRunningFilter => Filter == ContainerFilter.Running;

    /// <summary>筛选:已停止。</summary>
    public bool IsStoppedFilter => Filter == ContainerFilter.Stopped;

    /// <summary>筛选:异常。</summary>
    public bool IsProblemFilter => Filter == ContainerFilter.Problem;

    /// <summary>搜索词(名字 / 镜像 / 项目)。</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value))
            {
                ApplyView();
            }
        }
    }

    /// <summary>全部数量。</summary>
    public int TotalCount => _all.Count;

    /// <summary>运行中数量。</summary>
    public int RunningCount => _all.Count(r => r.IsRunning);

    /// <summary>已停止数量。</summary>
    public int StoppedCount => _all.Count(r => !r.IsRunning && !r.IsPaused);

    /// <summary>异常数量。</summary>
    public int ProblemCount => _all.Count(r => r.IsUnhealthy || r.IsFailed);

    /// <summary>
    /// 筛选弹层里的 compose 项目清单(设计稿 19 号板)。
    /// <para>
    /// 状态那一档已经在工具条的分段控件上,所以弹层里只放项目 ——
    /// 同一个筛选出现在两个地方,用户改了一处就再也说不清当前到底筛掉了什么。
    /// </para>
    /// </summary>
    public ObservableCollection<ProjectFilterItem> ProjectFilters { get; } = [];

    /// <summary>当前项目筛选;<see langword="null" /> 表示不按项目筛。</summary>
    public string? ProjectFilter
    {
        get => _project;
        private set
        {
            if (SetField(ref _project, value))
            {
                OnPropertiesChanged(nameof(HasProjectFilter), nameof(ProjectFilterLabel));
                ApplyView();
            }
        }
    }

    /// <summary>按项目筛着没有。</summary>
    public bool HasProjectFilter => _project is not null;

    /// <summary>筛选按钮上显示的文字。</summary>
    public string ProjectFilterLabel => _project switch
    {
        null => "项目",
        "" => "不属于任何项目",
        _ => _project
    };

    /// <summary>选一个项目来筛;参数为 <see langword="null" /> 时清除。</summary>
    public RelayCommand SetProjectFilterCommand => _setProject ??= new(p =>
    {
        ProjectFilter = p as string;
        return Task.CompletedTask;
    });

    private RelayCommand? _setProject;

    /// <summary>清除项目筛选。</summary>
    public RelayCommand ClearProjectFilterCommand => _clearProject ??= new(_ =>
    {
        ProjectFilter = null;
        return Task.CompletedTask;
    });

    private RelayCommand? _clearProject;

    private void BuildProjectFilters()
    {
        ProjectFilters.Clear();
        foreach (IGrouping<string, ContainerRow> group in _all
                     .GroupBy(r => r.Project)
                     .Where(g => g.Key.Length > 0)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ProjectFilters.Add(new(group.Key, group.Key, group.Count()));
        }
        int loose = _all.Count(r => r.Project.Length == 0);
        if (loose > 0)
        {
            ProjectFilters.Add(new("", "(不属于任何项目)", loose));
        }
        // 筛着的项目没了(最后一个容器被删了)就自动松开,否则列表会空得莫名其妙。
        if (_project is { } current && ProjectFilters.All(p => p.Name != current))
        {
            ProjectFilter = null;
        }
    }

    /// <summary>已勾选的数量。</summary>
    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetField(ref _selectedCount, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(SelectionText));
            }
        }
    }

    /// <summary>有没有勾选。</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>选中条上的文字。</summary>
    public string SelectionText => $"已选 {SelectedCount} 个容器";

    /// <summary>
    /// 列头那枚全选框。作用范围是**当前可见的**那些行,不是 _all ——
    /// 筛到「已停止 5」时按下全选,用户要的是这 5 个;
    /// 把背后那 13 个运行中的一起勾上,下一步「删除」就成了事故。
    /// 部分勾选时停在第三态,再按一次全勾上。
    /// </summary>
    public bool? AllSelected
    {
        get
        {
            if (View.Count == 0)
            {
                return false;
            }
            int picked = View.Count(r => r.Selected);
            return picked == 0 ? false : picked == View.Count ? true : null;
        }
        set
        {
            bool select = value is true;
            foreach (ContainerRow row in View)
            {
                row.Selected = select;
            }
            RecountSelection();
        }
    }

    /// <summary>列表是不是空的(且已经加载过)。</summary>
    public bool IsEmpty => LoadedOnce && _all.Count == 0;

    /// <summary>筛完之后没有匹配项。</summary>
    public bool NoMatch => LoadedOnce && _all.Count > 0 && View.Count == 0;

    /// <summary>详情抽屉;没打开时为 <see langword="null" />。</summary>
    public ContainerDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            if (SetField(ref _detail, value))
            {
                OnPropertiesChanged(nameof(HasDetail), nameof(CanResizeDrawer));
            }
        }
    }

    /// <summary>详情抽屉开着没有。</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>
    /// 列表的列宽。列头与几百行数据行共用这一份 —— 拖列头的手柄改的就是它。
    /// <para>
    /// 放在**页面**上而不是行上:列宽是这张表的属性,不是某一行的。
    /// </para>
    /// </summary>
    public ContainerColumns Columns { get; } = new();

    /// <summary>
    /// 详情抽屉的宽度(用户拖出来的)。
    /// <para>
    /// 放在页面上而不是抽屉自己身上:换一个容器就是换一个抽屉视图模型,
    /// 宽度要是跟着抽屉走,用户每点一行就得重拖一次。
    /// </para>
    /// <para>
    /// 视图与它是双向的:打开 / 还原时由视图把这个值写进列定义,
    /// 用户拖完(<c>DragCompleted</c>)再由视图把拖出来的实际宽度写回来 ——
    /// 与宿主侧栏里那两条分割条的做法一致。上界由列定义的 MaxWidth 兜着,
    /// 因为"面板有多宽"只有视图知道。
    /// </para>
    /// </summary>
    public double DrawerWidth
    {
        get => _drawerWidth;
        set => SetField(ref _drawerWidth, Math.Max(MinDrawerWidth, value));
    }

    /// <summary>抽屉再窄就摆不下头里那一行了。</summary>
    public static double MinimumDrawerWidth => MinDrawerWidth;

    /// <summary>抽屉最大化(占满整个页签)。</summary>
    public bool DetailMaximized
    {
        get => _detailMaximized;
        set
        {
            if (SetField(ref _detailMaximized, value))
            {
                OnPropertiesChanged(nameof(ListVisible), nameof(CanResizeDrawer));
            }
        }
    }

    /// <summary>抽屉的分割条要不要露出来。</summary>
    public bool CanResizeDrawer => HasDetail && !DetailMaximized;

    /// <summary>列表这一块要不要露出来(日志模式取代它,抽屉最大化盖住它)。</summary>
    public bool ListVisible => !IsLogsMode && !DetailMaximized;

    /// <summary>
    /// 把抽屉至少撑到这么宽。
    /// <para>
    /// 文件页那种三栏在 440px 里根本摆不开;但"撑满整个页签"又太狠 ——
    /// 撑到够用就停,列表还在旁边,分割条也还在,用户随时能往回拖。
    /// </para>
    /// </summary>
    public void EnsureDrawerAtLeast(double width) => DrawerWidth = Math.Max(DrawerWidth, width);

    /// <summary>切筛选。</summary>
    public RelayCommand SetFilterCommand { get; }

    /// <summary>打开详情。</summary>
    public RelayCommand OpenDetailCommand { get; }

    /// <summary>关掉详情。</summary>
    public RelayCommand CloseDetailCommand { get; }

    /// <summary>取消勾选。</summary>
    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>启动。</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>停止。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>重启。</summary>
    public RelayCommand RestartCommand { get; }

    /// <summary>暂停。</summary>
    public RelayCommand PauseCommand { get; }

    /// <summary>恢复。</summary>
    public RelayCommand UnpauseCommand { get; }

    /// <summary>强杀。</summary>
    public RelayCommand KillCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>
    /// 「运行容器…」:去镜像页挑一个。
    /// 不直接开运行表单 —— 那张表单的镜像字段是只读的,得先有镜像才谈得上运行。
    /// </summary>
    public RelayCommand RunFromImagesCommand => _runFromImages ??= new(_ => Shell.GoToAsync(PanelPage.Images));

    private RelayCommand? _runFromImages;

    /// <summary>工具条上的「拉取镜像」:和镜像页那颗是同一个对话框。</summary>
    public RelayCommand PullImageCommand => _pullImage ??= new(_ => Shell.ShowPullDialogAsync(null));

    private RelayCommand? _pullImage;

    /// <summary>空列表那一屏的「从 compose 起一套」。</summary>
    public RelayCommand GoComposeCommand => _goCompose ??= new(_ => Shell.GoToAsync(PanelPage.Compose));

    private RelayCommand? _goCompose;

    /// <inheritdoc />
    public override async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        StartSampling();
    }

    /// <summary>
    /// 开始(或重启)统计采样。
    /// <para>
    /// 采样<b>不</b>只在容器页跑:总览页的 CPU / 内存两张卡和 Top 5 都吃这一份数据,
    /// 而用户完全可能整段时间都待在总览页。所以连上之后就起,断开才停。
    /// </para>
    /// <para>
    /// 采的是 <c>_all</c> 而不是 <c>View</c>:后者被搜索框和筛选档裁过,
    /// 用它算出来的"CPU 总占用"会跟着用户输入的关键字变 —— 那不是一个总量。
    /// </para>
    /// </summary>
    public void StartSampling()
    {
        if (!Shell.Settings.InlineSparklines)
        {
            _sampler.Stop();
            Shell.Overview.SetStatsDisabled();
            return;
        }
        _sampler.Start(() => [.. _all.Where(r => r.IsRunning)],
            rows => Shell.Overview.AcceptStatsSnapshot(rows), Shell.Lifetime);
    }

    /// <summary>停止采样(断开时)。</summary>
    public void StopSampling() => _sampler.Stop();

    /// <summary>
    /// 连上之后在后台把容器列表读一遍并起采样 —— 让总览页不必等用户先逛一趟容器页。
    /// </summary>
    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        if (_all.Count == 0)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Shell.Context.Log.Warn($"containers: prime failed: {ex.Message}");
                return;
            }
        }
        StartSampling();
    }

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
            ContainerSummary[] summaries = await client.ListContainersAsync(true, cancellationToken).ConfigureAwait(true);
            // 在跑的排前面,同组内按名字 —— 运维找的十有八九是正在跑的那几个。
            List<ContainerRow> incoming =
            [
                .. summaries
                    .OrderByDescending(s => s.State == "running")
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new ContainerRow(s))
            ];
            Dictionary<string, ContainerRow> previous = _all.ToDictionary(r => r.Id);
            _all.Clear();
            foreach (ContainerRow row in incoming)
            {
                if (previous.TryGetValue(row.Id, out ContainerRow? existing))
                {
                    existing.Update(row);
                    _all.Add(existing);
                }
                else
                {
                    row.SelectionChanged += RecountSelection;
                    // 右键菜单要经这个回引找到页面的命令(菜单弹在独立的 popup 树里)。
                    row.Owner = this;
                    _all.Add(row);
                }
            }
            LoadedOnce = true;
            BuildProjectFilters();
            ApplyView();
            OnPropertiesChanged(nameof(TotalCount), nameof(RunningCount), nameof(StoppedCount), nameof(ProblemCount));
            Shell.SetContainerCount(_all.Count);
            if (Detail is { } detail && _all.FirstOrDefault(r => r.Id == detail.ContainerId) is { } updated)
            {
                detail.ApplySummary(updated);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        CloseDetail(force: true);
        _ = ExitLogsModeAsync();
        _sampler.Stop();
        _all.Clear();
        View.Clear();
        LoadedOnce = false;
        SelectedCount = 0;
        OnPropertiesChanged(nameof(TotalCount), nameof(RunningCount), nameof(StoppedCount), nameof(ProblemCount),
            nameof(IsEmpty), nameof(NoMatch));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type == "container";

    private void ApplyView()
    {
        string needle = _search.Trim();
        IEnumerable<ContainerRow> filtered = _all.Where(row => _filter switch
        {
            ContainerFilter.Running => row.IsRunning,
            ContainerFilter.Stopped => !row.IsRunning && !row.IsPaused,
            ContainerFilter.Problem => row.IsUnhealthy || row.IsFailed,
            _ => Shell.Settings.ShowStopped || row.IsRunning || row.IsPaused
        });
        if (_project is { } project)
        {
            filtered = filtered.Where(row => row.Project == project);
        }
        if (needle.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Image.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Project.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Id.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        }
        View.Merge([.. filtered], (_, _) => { });
        OnPropertiesChanged(nameof(IsEmpty), nameof(NoMatch), nameof(AllSelected));
    }

    private void RecountSelection()
    {
        SelectedCount = _all.Count(r => r.Selected);
        OnPropertyChanged(nameof(AllSelected));
    }

    private void ClearSelection()
    {
        foreach (ContainerRow row in _all.Where(r => r.Selected))
        {
            row.Selected = false;
        }
        SelectedCount = 0;
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>
    /// 命令参数是单行时只作用于那一行,否则作用于全部勾选项。
    /// <para>
    /// 这条规则让"行内按钮"与"选中条按钮"共用一套命令 —— 行内那颗按的是它自己,
    /// 顶上那颗按的是所有勾选的。用户不必猜哪颗按钮管哪些容器。
    /// </para>
    /// </summary>
    private IReadOnlyList<ContainerRow> Targets(object? parameter) =>
        parameter is ContainerRow row ? [row] : [.. _all.Where(r => r.Selected)];

    private async Task BatchAsync(string verb, IReadOnlyList<ContainerRow> targets,
        Func<DockerClient, string, CancellationToken, Task> action)
    {
        if (Client is not { } client || targets.Count == 0)
        {
            return;
        }
        foreach (ContainerRow row in targets)
        {
            row.Busy = true;
        }
        PanelTask task = Shell.Tasks.Start("Docker.box", $"{verb} {targets.Count} 个容器", indeterminate: targets.Count == 1);
        // 选中条原地变进度条:不弹窗、不遮列表,选中也不丢。
        BatchProgress = new(verb, targets.Count, task);
        try
        {
            BatchResult result = await BatchRunner.RunAsync(
                [.. targets.Select(r => (Target: r, r.Name))],
                (row, ct) => action(client, row.Id, ct),
                (done, total, current) =>
                {
                    task.Progress = total == 0 ? 0 : (double)done / total;
                    task.Indeterminate = total == 1;
                    task.Detail = current.Length > 0 ? $"{done}/{total} · 当前:{current}" : $"{done}/{total}";
                    Ui.Post(() => BatchProgress?.Advance(done, current, DescribeWait(verb, current)));
                },
                task.Token).ConfigureAwait(true);
            task.Finish(
                result.AllSucceeded ? PanelTaskState.Succeeded : PanelTaskState.PartiallyFailed,
                result.AllSucceeded ? "完成" : $"成功 {result.SucceededCount} · 失败 {result.FailedCount}");
            task.Payload = result;
            Shell.Feedback.ReportBatch(verb, result, inView: Shell.CurrentPage == PanelPage.Containers,
                () => Shell.TaskCenterOpen = true);
            // 有失败的就把结果留在选中条上,带一个「重试失败的 N 个」——
            // 让用户为了重试去翻任务中心,是把最想立刻做的那件事藏起来。
            if (!result.AllSucceeded)
            {
                BatchOutcome[] failures = [.. result.Failures];
                BatchSummary = new(verb, result, [.. targets.Where(t => failures.Any(f => f.Target == t.Name))],
                    action);
            }
            BatchProgress = null;
        }
        finally
        {
            BatchProgress = null;
            foreach (ContainerRow row in targets)
            {
                row.Busy = false;
            }
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
    }

    /// <summary>「当前在等什么」——- 说清楚比只报一个百分比有用。</summary>
    private static string DescribeWait(string verb, string current) => current.Length == 0 ? "" : verb switch
    {
        "停止" => $"{current} · 等待 SIGTERM(10s 超时)",
        "重启" => $"{current} · 等待重新启动",
        "删除" => $"{current} · 等待可写层被回收",
        _ => current
    };

    /// <summary>批量进行中的原地进度;没有时为 <see langword="null" />。</summary>
    public BatchProgressState? BatchProgress
    {
        get => _batchProgress;
        private set
        {
            if (SetField(ref _batchProgress, value))
            {
                OnPropertyChanged(nameof(HasBatchProgress));
            }
        }
    }

    private BatchProgressState? _batchProgress;

    /// <summary>批量正在跑。</summary>
    public bool HasBatchProgress => BatchProgress is not null;

    /// <summary>批量结束后的结果条(只在有失败时出现)。</summary>
    public BatchSummaryState? BatchSummary
    {
        get => _batchSummary;
        private set
        {
            if (SetField(ref _batchSummary, value))
            {
                OnPropertyChanged(nameof(HasBatchSummary));
            }
        }
    }

    private BatchSummaryState? _batchSummary;

    /// <summary>有失败结果要展示。</summary>
    public bool HasBatchSummary => BatchSummary is not null;

    /// <summary>关掉结果条。</summary>
    public RelayCommand DismissBatchCommand => _dismissBatch ??= new(_ =>
    {
        BatchSummary = null;
        return Task.CompletedTask;
    });

    private RelayCommand? _dismissBatch;

    /// <summary>只对失败的那几个再跑一遍。</summary>
    public RelayCommand RetryFailedCommand => _retryFailed ??= new(_ =>
    {
        if (BatchSummary is not { } summary)
        {
            return Task.CompletedTask;
        }
        BatchSummary = null;
        return BatchAsync(summary.Verb, summary.FailedRows, summary.Action);
    });

    private RelayCommand? _retryFailed;

    private async Task KillAsync(IReadOnlyList<ContainerRow> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = targets.Count == 1 ? $"强制杀死 {targets[0].Name}?" : $"强制杀死 {targets.Count} 个容器?",
            Icon = "Icon.zap",
            HostName = "",
            ConfirmLabel = "强制杀死",
            ConfirmIcon = "Icon.zap",
            Commands = [.. targets.Select(t => $"POST /containers/{t.Name}/kill?signal=SIGKILL")],
            CommandNote = $"等价于  docker kill {string.Join(' ', targets.Select(t => t.Name))}",
            Targets = [.. targets.Select(ToConfirmTarget)],
            Consequences =
            [
                new(3, "SIGKILL 不给进程刷缓冲、关连接的机会 —— 写到一半的数据会丢。"),
                new(0, "除非它已经卡死,否则该用「停止」:那会先发 SIGTERM,等 10 秒再动手。")
            ]
        })).ConfigureAwait(true);
        if (confirmed)
        {
            await BatchAsync("强杀", targets, (c, id, ct) => c.KillContainerAsync(id, "SIGKILL", ct)).ConfigureAwait(true);
        }
    }

    private async Task RemoveAsync(IReadOnlyList<ContainerRow> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }
        bool anyRunning = targets.Any(t => t.IsRunning);
        string[] projects = [.. targets.Select(t => t.Project).Where(p => p.Length > 0).Distinct()];
        List<ConfirmConsequence> consequences =
        [
            new(3, "容器及其可写层会被删除,写在容器里(不在卷里)的数据丢失。"),
            new(1, "挂载的命名卷不受影响 —— 本次不带 -v。")
        ];
        if (anyRunning)
        {
            consequences.Insert(0, new(2, "其中有正在运行的容器,会先被强制停止。"));
        }
        if (projects.Length > 0)
        {
            consequences.Add(new(0,
                $"它们属于 compose 项目 {string.Join('、', projects)},下次 up -d 会按 compose.yaml 重建。"));
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = targets.Count == 1 ? $"删除容器 {targets[0].Name}?" : $"删除 {targets.Count} 个容器?",
            Icon = "Icon.trash-2",
            HostName = "",
            ConfirmLabel = targets.Count == 1 ? "删除容器" : $"删除 {targets.Count} 个容器",
            Commands = [.. targets.Select(t => $"DELETE /containers/{t.Name}?v=false&force={(anyRunning ? "true" : "false")}")],
            CommandNote = $"等价于  docker rm {(anyRunning ? "-f " : "")}{string.Join(' ', targets.Select(t => t.Name))}",
            Targets = [.. targets.Select(ToConfirmTarget)],
            Consequences = consequences
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        CloseDetailIfRemoved(targets);
        await BatchAsync("删除", targets, (c, id, ct) => c.RemoveContainerAsync(id, anyRunning, false, ct)).ConfigureAwait(true);
        ClearSelection();
    }

    private void CloseDetailIfRemoved(IReadOnlyList<ContainerRow> targets)
    {
        // 钉住也拦不住这一条:容器都删了,留一个指向它的抽屉没有意义。
        if (Detail is { } detail && targets.Any(t => t.Id == detail.ContainerId))
        {
            CloseDetail(force: true);
        }
    }

    private static ConfirmTarget ToConfirmTarget(ContainerRow row) =>
        new(row.Name, row.Image, row.Status, row.IsRunning);

    // ── 日志模式 ──────────────────────────────────────────────────

    /// <summary>
    /// 合并日志视图;<see langword="null" /> 表示当前是普通列表模式。
    /// <para>
    /// 它取代**列表**而不是叠在上面:合并多条流时左边要放来源选择器,
    /// 而那块地方原本就是列表的。
    /// </para>
    /// </summary>
    public LogsViewModel? MergedLogs
    {
        get => _mergedLogs;
        private set
        {
            if (SetField(ref _mergedLogs, value))
            {
                OnPropertiesChanged(nameof(IsLogsMode), nameof(ListVisible));
            }
        }
    }

    /// <summary>在日志模式。</summary>
    public bool IsLogsMode => MergedLogs is not null;

    /// <summary>来源选择器里的条目。</summary>
    public ObservableCollection<LogSourceItem> LogSources { get; } = [];

    /// <summary>来源过滤词。</summary>
    public string LogSourceSearch
    {
        get => _logSourceSearch;
        set
        {
            if (SetField(ref _logSourceSearch, value))
            {
                ApplyLogSourceView();
            }
        }
    }

    /// <summary>选中的来源数。</summary>
    public int SelectedSourceCount => LogSources.Count(s => s.Selected);

    /// <summary>合并了几条流。</summary>
    public string MergedText => SelectedSourceCount switch
    {
        0 => "没有选中来源",
        1 => "1 条流",
        _ => $"合并 {SelectedSourceCount} 条流"
    };

    /// <summary>进入日志模式(用当前勾选的容器,没勾就用参数那一个)。</summary>
    public RelayCommand ViewLogsCommand => _viewLogs ??= new(p => EnterLogsModeAsync(Targets(p)));

    private RelayCommand? _viewLogs;

    /// <summary>回到列表。</summary>
    public RelayCommand ExitLogsCommand => _exitLogs ??= new(_ => ExitLogsModeAsync());

    private RelayCommand? _exitLogs;

    /// <summary>把某个来源从合并流里去掉(顶部 chip 上那个 ×)。</summary>
    public RelayCommand RemoveSourceCommand => _removeSource ??= new(p =>
    {
        if (p is LogSource source)
        {
            RemoveLogSource(source);
        }
        return Task.CompletedTask;
    });

    private RelayCommand? _removeSource;

    /// <summary>
    /// 从合并流里摘掉一条来源。
    /// 走勾选那条路,左边面板的状态跟着一起变 —— 两处不能各说各话。
    /// </summary>
    private void RemoveLogSource(LogSource source)
    {
        if (LogSources.FirstOrDefault(s => s.Source.ContainerId == source.ContainerId) is { } item)
        {
            item.Selected = false;
        }
    }

    /// <summary>来源全选 / 全不选。</summary>
    public RelayCommand ToggleAllSourcesCommand => _toggleAll ??= new(_ =>
    {
        bool select = LogSources.Any(s => !s.Selected);
        foreach (LogSourceItem item in LogSources)
        {
            item.Selected = select;
        }
        return Task.CompletedTask;
    });

    private RelayCommand? _toggleAll;

    private async Task EnterLogsModeAsync(IReadOnlyList<ContainerRow> seed)
    {
        BuildLogSources(seed);
        var logs = new LogsViewModel(Shell, SelectedSources())
        {
            // chip 上的 × 走勾选那条路,左边面板跟着一起变。
            SourceRemover = source =>
            {
                RemoveLogSource(source);
                return Task.CompletedTask;
            }
        };
        MergedLogs = logs;
        await logs.EnsureStartedAsync().ConfigureAwait(true);
    }

    private async Task ExitLogsModeAsync()
    {
        if (MergedLogs is { } logs)
        {
            MergedLogs = null;
            await logs.DisposeAsync().ConfigureAwait(true);
        }
        foreach (LogSourceItem item in LogSources)
        {
            item.SelectionChanged -= OnLogSourceToggled;
        }
        LogSources.Clear();
    }

    private void BuildLogSources(IReadOnlyList<ContainerRow> seed)
    {
        foreach (LogSourceItem existing in LogSources)
        {
            existing.SelectionChanged -= OnLogSourceToggled;
        }
        LogSources.Clear();
        HashSet<string> seeded = [.. seed.Select(r => r.Id)];
        // 全部容器都列出来(含已停止的)—— 已停止容器的日志依然读得到,
        // 而"为什么它退出了"恰恰是最常要看的一份日志。
        int index = 0;
        foreach (ContainerRow row in _all)
        {
            var item = new LogSourceItem(
                new(row.Id, row.Name, row.Summary.State == "running"),
                row.IsRunning ? "运行中" : row.IsPaused ? "已暂停" : row.Uptime,
                row.Tone,
                index++)
            {
                Selected = seeded.Contains(row.Id)
            };
            item.SelectionChanged += OnLogSourceToggled;
            LogSources.Add(item);
        }
        ApplyLogSourceView();
    }

    private void OnLogSourceToggled()
    {
        OnPropertiesChanged(nameof(SelectedSourceCount), nameof(MergedText));
        // 勾选变化直接换流。清屏是有意的 —— 合并流按到达顺序排,
        // 留着旧行再补新来源的 tail,拼出来的时间线是假的。
        _ = MergedLogs?.SetSourcesAsync(SelectedSources());
    }

    private List<LogSource> SelectedSources()
    {
        // 颜色序号按**选中顺序里的位置**重新编号,而不是用列表里的下标:
        // 否则只选两个相邻容器时会拿到两个几乎一样的颜色。
        List<LogSourceItem> selected = [.. LogSources.Where(s => s.Selected)];
        for (int i = 0; i < selected.Count; i++)
        {
            selected[i].Index = i;
        }
        return [.. selected.Select(s => s.Source)];
    }

    private void ApplyLogSourceView()
    {
        string needle = _logSourceSearch.Trim();
        foreach (LogSourceItem item in LogSources)
        {
            item.Visible = needle.Length == 0
                           || item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 行级动作(右键菜单 / 命令面板都走这几个)────────────────────

    /// <summary>打开详情并停在指定页签。</summary>
    public RelayCommand OpenTabCommand => _openTab ??= new(p =>
        p is object[] { Length: 2 } args && args[0] is ContainerRow row && args[1] is DetailTab tab
            ? OpenDetailAtTabAsync(row, tab)
            : Task.CompletedTask);

    private RelayCommand? _openTab;

    /// <summary>看这一个容器的日志(详情抽屉的日志页签)。</summary>
    public RelayCommand RowLogsCommand => _rowLogs ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Logs) : Task.CompletedTask);

    private RelayCommand? _rowLogs;

    /// <summary>进这一个容器的终端。</summary>
    public RelayCommand RowTerminalCommand => _rowTerminal ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Terminal) : Task.CompletedTask);

    private RelayCommand? _rowTerminal;

    /// <summary>浏览这一个容器的文件。</summary>
    public RelayCommand RowFilesCommand => _rowFiles ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Files) : Task.CompletedTask);

    private RelayCommand? _rowFiles;

    /// <summary>看这一个容器的实时统计。</summary>
    public RelayCommand RowStatsCommand => _rowStats ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Stats) : Task.CompletedTask);

    private RelayCommand? _rowStats;

    /// <summary>重命名。</summary>
    public RelayCommand RowRenameCommand => _rowRename ??= new(p =>
        p is ContainerRow row ? RowDetailActionAsync(row, d => d.RenameCommand) : Task.CompletedTask);

    private RelayCommand? _rowRename;

    /// <summary>改重启策略。</summary>
    public RelayCommand RowRestartPolicyCommand => _rowPolicy ??= new(p =>
        p is ContainerRow row ? RowDetailActionAsync(row, d => d.RestartPolicyCommand) : Task.CompletedTask);

    private RelayCommand? _rowPolicy;

    /// <summary>复制容器 id。</summary>
    public RelayCommand RowCopyIdCommand => _rowCopyId ??= new(p =>
        p is ContainerRow row ? CopyAsync(row.Id, "容器 ID") : Task.CompletedTask);

    private RelayCommand? _rowCopyId;

    /// <summary>复制等价的 <c>docker run</c> 命令。</summary>
    public RelayCommand RowCopyRunCommand => _rowCopyRun ??= new(p =>
        p is ContainerRow row ? CopyRunCommandAsync(row) : Task.CompletedTask);

    private RelayCommand? _rowCopyRun;

    private async Task OpenDetailAtTabAsync(ContainerRow row, DetailTab tab)
    {
        await OpenDetailAsync(row).ConfigureAwait(true);
        if (Detail is { } detail)
        {
            await detail.GoToTabAsync(tab).ConfigureAwait(true);
        }
    }

    /// <summary>菜单里那几项其实住在详情抽屉上 —— 先把抽屉打开,再借它的命令。</summary>
    private async Task RowDetailActionAsync(ContainerRow row, Func<ContainerDetailViewModel, RelayCommand> pick)
    {
        await OpenDetailAsync(row).ConfigureAwait(true);
        if (Detail is { } detail)
        {
            pick(detail).Execute(null);
        }
    }

    private async Task CopyAsync(string text, string what)
    {
        await Shell.Context.Clipboard.SetTextAsync(text, Shell.Lifetime).ConfigureAwait(true);
        Shell.Feedback.Status(FeedbackKind.Success, $"已复制{what}");
    }

    /// <summary>
    /// 反推 <c>docker run</c> 并复制。
    /// <para>
    /// 会顺带读一次镜像的 inspect —— 为的是把镜像自带的环境变量减掉。
    /// 不减的话复制出来的命令会拖着十几条 <c>PATH</c> / <c>LANG</c>,那些不是用户写的。
    /// </para>
    /// </summary>
    private async Task CopyRunCommandAsync(ContainerRow row)
    {
        if (Client is not { } client)
        {
            return;
        }
        try
        {
            ContainerInspect inspect = await client.InspectContainerAsync(row.Id, Shell.Lifetime).ConfigureAwait(true);
            string[]? imageEnv = null;
            if (inspect.Config?.Image is { Length: > 0 } image)
            {
                try
                {
                    ImageInspect imageInspect = await client.InspectImageAsync(image, Shell.Lifetime).ConfigureAwait(true);
                    imageEnv = imageInspect.Config?.Env;
                }
                catch (Exception)
                {
                    // 镜像已经被删了(容器还在跑)也很常见 —— 那就不减,多几条环境变量而已。
                }
            }
            string command = RunCommandBuilder.Build(inspect, imageEnv);
            await Shell.Context.Clipboard
                .SetTextAsync($"{command}\n{RunCommandBuilder.Caveat}", Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "已复制 docker run 命令",
                "由 inspect 反推,是近似值 —— 执行前请核对(提醒已一并复制)。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("生成 docker run 命令", ex);
        }
    }

    /// <summary>打开某个容器的详情,并把文件页落到指定目录(卷页借道过来时用)。</summary>
    public async Task OpenDetailAtFilesAsync(ContainerRow row, string path)
    {
        await OpenDetailAsync(row).ConfigureAwait(true);
        if (Detail is { } detail)
        {
            await detail.OpenFilesAtAsync(path).ConfigureAwait(true);
        }
    }

    private async Task OpenDetailAsync(ContainerRow row)
    {
        if (Detail is { } existing && existing.ContainerId == row.Id)
        {
            return;
        }
        // 换容器时钉住也要让位 —— 用户明确点了另一行。
        CloseDetail(force: true);
        var detail = new ContainerDetailViewModel(Shell, this, row);
        Detail = detail;
        MarkCurrent(row.Id);
        await detail.LoadAsync(Shell.Lifetime).ConfigureAwait(true);
    }

    /// <summary>关抽屉。钉住的抽屉只有 <paramref name="force" /> 才关得掉。</summary>
    private void CloseDetail(bool force = false)
    {
        if (Detail is { } detail && (force || !detail.Pinned))
        {
            _ = detail.DisposeAsync();
            Detail = null;
            // 最大化是抽屉的状态,抽屉没了它也就该没了 ——
            // 不然下一次开抽屉会莫名其妙地直接铺满整页。
            DetailMaximized = false;
            MarkCurrent(null);
        }
    }

    /// <summary>
    /// 把"抽屉里开着的那一行"标出来。走 _all 而不是 View ——
    /// 抽屉钉住时用户可能已经切走了筛选,那一行不在可见集里也仍然是当前行。
    /// </summary>
    private void MarkCurrent(string? id)
    {
        foreach (ContainerRow row in _all)
        {
            row.Current = row.Id == id;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CloseDetail(force: true);
        await ExitLogsModeAsync().ConfigureAwait(false);
        await _sampler.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 批量进行中的原地进度(选中条变成的那一条)。
/// <para>
/// 设计稿 17 号板要求的形态:同一条位置不动,内容从"已选 10 个容器 + 五个动作"
/// 变成"正在停止 10 个容器… 6/10 · 当前:worker-2 · 等待 SIGTERM" + 取消剩余。
/// 不弹窗、不遮列表、选中不丢。
/// </para>
/// </summary>
public sealed class BatchProgressState(string verb, int total, PanelTask task) : ObservableObject
{
    private int _done;
    private string _wait = "";

    /// <summary>动作名。</summary>
    public string Verb { get; } = verb;

    /// <summary>目标总数。</summary>
    public int Total { get; } = total;

    /// <summary>已完成数。</summary>
    public int Done
    {
        get => _done;
        private set
        {
            if (SetField(ref _done, value))
            {
                OnPropertiesChanged(nameof(CountText), nameof(Progress));
            }
        }
    }

    /// <summary>"6 / 10"。</summary>
    public string CountText => $"{Done} / {Total}";

    /// <summary>0–1。</summary>
    public double Progress => Total == 0 ? 0 : (double)Done / Total;

    /// <summary>标题。</summary>
    public string Title => $"正在{Verb} {Total} 个容器…";

    /// <summary>当前在等什么。</summary>
    public string WaitText
    {
        get => _wait;
        private set => SetField(ref _wait, value);
    }

    /// <summary>取消剩下的。</summary>
    public RelayCommand CancelCommand => _cancel ??= new(_ =>
    {
        task.Cancel();
        return Task.CompletedTask;
    });

    private RelayCommand? _cancel;

    /// <summary>推进一格。</summary>
    public void Advance(int done, string current, string wait)
    {
        Done = done;
        WaitText = wait.Length > 0 ? $"当前:{wait}" : current;
    }
}

/// <summary>
/// 批量结束后的结果条。只在**有失败**时出现 —— 全成功的时候状态栏那一句就够了,
/// 再留一条要用户手动关掉的横幅是在收税。
/// </summary>
public sealed class BatchSummaryState(
    string verb,
    BatchResult result,
    IReadOnlyList<ContainerRow> failedRows,
    Func<DockerClient, string, CancellationToken, Task> action)
{
    /// <summary>动作名。</summary>
    public string Verb { get; } = verb;

    /// <summary>失败的那几行(重试用)。</summary>
    public IReadOnlyList<ContainerRow> FailedRows { get; } = failedRows;

    /// <summary>原来那个动作(重试用)。</summary>
    public Func<DockerClient, string, CancellationToken, Task> Action { get; } = action;

    /// <summary>标题。</summary>
    public string Title => $"已{Verb} {result.SucceededCount} 个,{result.FailedCount} 个失败";

    /// <summary>重试按钮上的文字。</summary>
    public string RetryText => $"重试失败的 {result.FailedCount} 个";

    /// <summary>失败的逐条明细。成功的那些不列 —— 它们不需要用户做任何事。</summary>
    public IReadOnlyList<BatchOutcome> Failures { get; } = [.. result.Failures];

    /// <summary>"+ N 个成功已折叠"。</summary>
    public string CollapsedText => result.SucceededCount > 0 ? $"+ {result.SucceededCount} 个成功已折叠" : "";

    /// <summary>有折叠起来的成功项。</summary>
    public bool HasCollapsed => result.SucceededCount > 0;
}


/// <summary>
/// 容器列表的列宽。
/// <para>
/// 与宿主的文件浏览器同一路数:列宽是一组 <see cref="GridLength" />,
/// **列头和每一行的 ColumnDefinitions 绑的是同一份实例** ——
/// Avalonia 没有 SharedSizeGroup,这是让列头与几百行单元格始终对齐的唯一办法。
/// </para>
/// <para>
/// 六列全是定宽可拖的,富余的宽度交给「运行时长」与行尾动作之间的填充列。
/// 不留弹性列:弹性列的边界是**算**出来的、拖不动,
/// 而它往往正是最想拖的那一条(名称跟着窗口长到一千多像素,就是这么来的)。
/// </para>
/// </summary>
public sealed class ContainerColumns : ObservableObject
{
    /// <summary>列与列之间那条拖拽轨道的宽度。</summary>
    public const double SplitterWidth = 6;

    /// <summary>可拖的列,次序与界面一致。</summary>
    public static readonly string[] Keys = ["name", "image", "ports", "cpu", "mem", "uptime"];

    private GridLength _name = new(240);
    private GridLength _image = new(260);
    private GridLength _ports = new(118);
    private GridLength _cpu = new(108);
    private GridLength _mem = new(84);
    private GridLength _uptime = new(78);

    /// <summary>名称列。</summary>
    public GridLength Name
    {
        get => _name;
        set => SetField(ref _name, Clamp(value, "name"));
    }

    /// <summary>镜像列。带 registry 前缀的镜像名本来就长,默认给得比设计稿宽一档。</summary>
    public GridLength Image
    {
        get => _image;
        set => SetField(ref _image, Clamp(value, "image"));
    }

    /// <summary>端口列。</summary>
    public GridLength Ports
    {
        get => _ports;
        set => SetField(ref _ports, Clamp(value, "ports"));
    }

    /// <summary>CPU 列。</summary>
    public GridLength Cpu
    {
        get => _cpu;
        set => SetField(ref _cpu, Clamp(value, "cpu"));
    }

    /// <summary>内存列。</summary>
    public GridLength Mem
    {
        get => _mem;
        set => SetField(ref _mem, Clamp(value, "mem"));
    }

    /// <summary>运行时长列。</summary>
    public GridLength Uptime
    {
        get => _uptime;
        set => SetField(ref _uptime, Clamp(value, "uptime"));
    }

    /// <summary>某一列的下限。再窄就只剩省略号了。</summary>
    public static double MinWidthFor(string key) => key switch
    {
        "name" => 140,
        "image" => 120,
        "ports" => 70,
        "cpu" => 78,
        "mem" => 62,
        _ => 62
    };

    /// <summary>某一列双击自适应时的上限。</summary>
    public static double MaxAutoFitFor(string key) => key is "name" or "image" ? 760 : 300;

    /// <summary>按名字读一列的宽度(拖拽代码用 Tag 认列)。</summary>
    public double GetColumnWidth(string key) => key switch
    {
        "name" => Name.Value,
        "image" => Image.Value,
        "ports" => Ports.Value,
        "cpu" => Cpu.Value,
        "mem" => Mem.Value,
        _ => Uptime.Value
    };

    /// <summary>按名字写一列的宽度。</summary>
    public void SetColumnWidth(string key, double width)
    {
        GridLength value = new(width);
        switch (key)
        {
            case "name": Name = value; break;
            case "image": Image = value; break;
            case "ports": Ports = value; break;
            case "cpu": Cpu = value; break;
            case "mem": Mem = value; break;
            case "uptime": Uptime = value; break;
        }
    }

    /// <summary>用户拖出来的列宽只可能是像素值 —— 星形和 Auto 在这里没有意义。</summary>
    private static GridLength Clamp(GridLength value, string key)
    {
        double min = MinWidthFor(key);
        double px = value.IsAbsolute ? value.Value : min;
        return new(Math.Max(min, px));
    }
}
