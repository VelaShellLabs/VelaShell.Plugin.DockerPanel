using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    private IReadOnlyList<ContainerItem> _allContainers = [];
    private IReadOnlyList<ContainerRow> _selectedContainers = [];
    private bool _showAllContainers = true;
    private bool _showContainerSize;
    private bool _showStats = true;

    /// <summary>当前显示的容器行(已过滤)。</summary>
    public ObservableCollection<ContainerRow> Containers { get; } = [];

    /// <summary>是否连已停止的容器一起列。</summary>
    public bool ShowAllContainers
    {
        get => _showAllContainers;
        set
        {
            if (SetProperty(ref _showAllContainers, value))
            {
                // 存的是反义。默认值是"全部都列",而一台从没设置过的机器读 bool 读出来是 false ——
                // 存正义的话,第一次打开面板就会把默认值悄悄翻成"只列在跑的"。
                _ = SaveSettingAsync("containersRunningOnly", !value);
                _ = RefreshActiveAsync(true);
            }
        }
    }

    /// <summary>是否顺带算可写层大小(慢)。</summary>
    public bool ShowContainerSize
    {
        get => _showContainerSize;
        set
        {
            if (SetProperty(ref _showContainerSize, value))
            {
                _ = SaveSettingAsync("showContainerSize", value);
                _ = RefreshActiveAsync(true);
            }
        }
    }

    /// <summary>是否顺带取 CPU / 内存(每次刷新多一条 <c>docker stats --no-stream</c>)。</summary>
    public bool ShowStats
    {
        get => _showStats;
        set
        {
            if (SetProperty(ref _showStats, value))
            {
                _ = SaveSettingAsync("statsOff", !value);
                _ = RefreshActiveAsync(true);
            }
        }
    }

    /// <summary>当前选中的容器行。</summary>
    public IReadOnlyList<ContainerRow> SelectedContainers => _selectedContainers;

    /// <summary>选中的第一行(抽屉对着它)。</summary>
    public ContainerRow? PrimaryContainer => _selectedContainers.Count > 0 ? _selectedContainers[0] : null;

    /// <summary>启动。</summary>
    public AsyncCommand StartContainersCommand { get; private set; } = null!;

    /// <summary>停止。</summary>
    public AsyncCommand StopContainersCommand { get; private set; } = null!;

    /// <summary>重启。</summary>
    public AsyncCommand RestartContainersCommand { get; private set; } = null!;

    /// <summary>暂停。</summary>
    public AsyncCommand PauseContainersCommand { get; private set; } = null!;

    /// <summary>恢复。</summary>
    public AsyncCommand UnpauseContainersCommand { get; private set; } = null!;

    /// <summary>强杀。</summary>
    public AsyncCommand KillContainersCommand { get; private set; } = null!;

    /// <summary>删除。</summary>
    public AsyncCommand RemoveContainersCommand { get; private set; } = null!;

    /// <summary>重命名。</summary>
    public AsyncCommand RenameContainerCommand { get; private set; } = null!;

    /// <summary>改重启策略。</summary>
    public AsyncCommand RestartPolicyCommand { get; private set; } = null!;

    /// <summary>把 <c>docker exec -it</c> 敲进终端。</summary>
    public AsyncCommand ShellContainerCommand { get; private set; } = null!;

    /// <summary>复制容器 id。</summary>
    public AsyncCommand CopyContainerIdCommand { get; private set; } = null!;

    /// <summary>视图在选中项变化时调这个(<c>ListBox.SelectedItems</c> 在 Avalonia 里不可绑定)。</summary>
    /// <param name="rows">当前选中的行。</param>
    public void SetContainerSelection(IReadOnlyList<ContainerRow> rows)
    {
        _selectedContainers = rows;
        RaisePropertyChanged(nameof(SelectedContainers));
        RaisePropertyChanged(nameof(PrimaryContainer));
        RaisePropertyChanged(nameof(SelectionSummary));
        RaiseContainerCommandStates();
        _ = LoadDrawerAsync(false);
    }

    private void BuildContainerCommands()
    {
        StartContainersCommand = new(() => LifecycleAsync("start", _loc["Container_Start"]), HasContainerSelection);
        StopContainersCommand = new(() => LifecycleAsync("stop", _loc["Container_Stop"]), HasContainerSelection);
        RestartContainersCommand = new(() => LifecycleAsync("restart", _loc["Container_Restart"]), HasContainerSelection);
        PauseContainersCommand = new(() => LifecycleAsync("pause", _loc["Container_Pause"]), HasContainerSelection);
        UnpauseContainersCommand = new(() => LifecycleAsync("unpause", _loc["Container_Unpause"]), HasContainerSelection);
        KillContainersCommand = new(KillContainersAsync, HasContainerSelection);
        RemoveContainersCommand = new(RemoveContainersAsync, HasContainerSelection);
        RenameContainerCommand = new(RenameContainerAsync, HasSingleContainer);
        RestartPolicyCommand = new(ChangeRestartPolicyAsync, HasContainerSelection);
        ShellContainerCommand = new(ShellIntoContainerAsync, HasSingleContainer);
        CopyContainerIdCommand = new(() => CopyAsync(PrimaryContainer?.Model.Id), HasSingleContainer);
    }

    private bool HasContainerSelection() => IsEngineReady && _selectedContainers.Count > 0;

    private bool HasSingleContainer() => IsEngineReady && _selectedContainers.Count == 1;

    private void RaiseContainerCommandStates()
    {
        StartContainersCommand.RaiseCanExecuteChanged();
        StopContainersCommand.RaiseCanExecuteChanged();
        RestartContainersCommand.RaiseCanExecuteChanged();
        PauseContainersCommand.RaiseCanExecuteChanged();
        UnpauseContainersCommand.RaiseCanExecuteChanged();
        KillContainersCommand.RaiseCanExecuteChanged();
        RemoveContainersCommand.RaiseCanExecuteChanged();
        RenameContainerCommand.RaiseCanExecuteChanged();
        RestartPolicyCommand.RaiseCanExecuteChanged();
        ShellContainerCommand.RaiseCanExecuteChanged();
        CopyContainerIdCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadContainersAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        // 列表 + 计数 + 统计一次往返拿回来。分三次调用在本机看不出差别,
        // 在一条 200ms 往返的链路上就是每次刷新多卡半秒。
        ContainerSnapshot snapshot = await GuardAsync(
            token => api.SnapshotContainersAsync(ShowAllContainers, ShowContainerSize, ShowStats, token)).ConfigureAwait(true);
        _allContainers = snapshot.Containers;
        PublishContainers();
        ApplyCounts(snapshot.Counts);
        ApplyStats(snapshot.Stats);
    }

    private void ApplyCounts(ContainerCounts counts) =>
        CountsText = counts.Containers < 0
            ? string.Empty
            : _loc.Format("Header_Counts", counts.Running, counts.Containers, counts.Images, counts.Volumes);

    private void ApplyStats(IReadOnlyDictionary<string, StatsItem> stats)
    {
        foreach (ContainerRow row in Containers)
        {
            // stats 回短 id,ps --no-trunc 回长 id:按短 id 对齐。
            row.ApplyStats(stats.TryGetValue(row.Model.ShortId, out StatsItem? item) ? item : null);
        }
    }

    private void PublishContainers()
    {
        List<ContainerItem> visible = [];
        foreach (ContainerItem item in _allContainers)
        {
            if (Matches(item.Name, item.Image, item.ShortId, item.State, item.Status, item.Ports, item.ComposeProject))
            {
                visible.Add(item);
            }
        }
        RowSync.Apply(Containers, visible, static c => c.Id, static c => new ContainerRow(c));
        RaisePropertyChanged(nameof(HasContainers));
        // 过滤把选中的行滤掉之后,选中集合里还留着一个不在列表上的行 —— 清掉,
        // 否则按下"删除"删的是一个用户已经看不见的容器。
        if (_selectedContainers.Count > 0)
        {
            IReadOnlyList<ContainerRow> kept = [.. _selectedContainers.Where(Containers.Contains)];
            if (kept.Count != _selectedContainers.Count)
            {
                SetContainerSelection(kept);
            }
        }
    }

    /// <summary>容器列表非空(界面据此决定画列表还是画"空"提示)。</summary>
    public bool HasContainers => Containers.Count > 0;

    /// <summary>状态栏左边那句"已选 N 项"。</summary>
    public string SelectionSummary => ActiveTab switch
    {
        DockerTab.Containers when _selectedContainers.Count > 0 => _loc.Format("Common_Selected", _selectedContainers.Count),
        DockerTab.Images when _selectedImages.Count > 0 => _loc.Format("Common_Selected", _selectedImages.Count),
        DockerTab.Volumes when _selectedVolumes.Count > 0 => _loc.Format("Common_Selected", _selectedVolumes.Count),
        DockerTab.Networks when _selectedNetworks.Count > 0 => _loc.Format("Common_Selected", _selectedNetworks.Count),
        _ => string.Empty
    };

    private IReadOnlyList<string> SelectedContainerIds => [.. _selectedContainers.Select(static r => r.Model.Id)];

    private async Task LifecycleAsync(string action, string label)
    {
        if (_api is not { } api || _selectedContainers.Count == 0)
        {
            return;
        }
        Status = _loc.Format("Status_Working", label);
        IReadOnlyList<BatchOutcome> outcomes = await GuardAsync(
            token => api.ContainerActionAsync(action, SelectedContainerIds, token)).ConfigureAwait(true);
        ReportBatch(label, outcomes);
        await LoadContainersAsync().ConfigureAwait(true);
    }

    private async Task KillContainersAsync()
    {
        if (_api is not { } api || _selectedContainers.Count == 0)
        {
            return;
        }
        ConfirmAnswer answer = await Confirm.AskAsync(
            _loc.Format("Confirm_Kill", _selectedContainers.Count),
            _loc["Confirm_KillBody"],
            DescribeTargets(_selectedContainers.Select(static r => r.Model.Name)),
            _loc["Container_Kill"],
            _loc["Common_Cancel"],
            true).ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        Status = _loc.Format("Status_Working", _loc["Container_Kill"]);
        IReadOnlyList<BatchOutcome> outcomes = await GuardAsync(
            token => api.ContainerActionAsync("kill", SelectedContainerIds, token)).ConfigureAwait(true);
        ReportBatch(_loc["Container_Kill"], outcomes);
        await LoadContainersAsync().ConfigureAwait(true);
    }

    private async Task RemoveContainersAsync()
    {
        if (_api is not { } api || _selectedContainers.Count == 0)
        {
            return;
        }
        bool anyRunning = _selectedContainers.Any(static r => r.Model.IsRunning);
        ConfirmAnswer answer = await Confirm.AskAsync(
            _loc.Format("Confirm_RemoveContainers", _selectedContainers.Count),
            _loc["Confirm_RemoveContainersBody"],
            DescribeTargets(_selectedContainers.Select(static r => r.Model.Name)),
            _loc["Container_Remove"],
            _loc["Common_Cancel"],
            true,
            optionLabel: _loc["Confirm_RemoveContainersVolumes"]).ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        Status = _loc.Format("Status_Working", _loc["Container_Remove"]);
        IReadOnlyList<BatchOutcome> outcomes = await GuardAsync(
            token => api.RemoveContainersAsync(SelectedContainerIds, anyRunning, answer.Option, token)).ConfigureAwait(true);
        ReportBatch(_loc["Container_Remove"], outcomes);
        SetContainerSelection([]);
        await LoadContainersAsync().ConfigureAwait(true);
    }

    private async Task RenameContainerAsync()
    {
        if (_api is not { } api || PrimaryContainer is not { } row)
        {
            return;
        }
        IReadOnlyDictionary<string, string>? values = await Form.AskAsync(
            _loc.Format("Form_Rename_Title", row.Model.Name),
            string.Empty,
            [PanelForm.Text("name", _loc["Form_Rename_Name"], row.Model.Name)],
            _loc["Form_Submit"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        string name = values.Text("name");
        if (name.Length == 0 || name == row.Model.Name)
        {
            return;
        }
        DockerResult result = await GuardAsync(token => api.RenameContainerAsync(row.Model.Id, name, token)).ConfigureAwait(true);
        ReportResult(_loc["Container_Rename"], result);
        await LoadContainersAsync().ConfigureAwait(true);
    }

    private async Task ChangeRestartPolicyAsync()
    {
        if (_api is not { } api || _selectedContainers.Count == 0)
        {
            return;
        }
        IReadOnlyDictionary<string, string>? values = await Form.AskAsync(
            _loc["Form_Policy_Title"],
            DescribeTargets(_selectedContainers.Select(static r => r.Model.Name)),
            [PanelForm.Choice("policy", _loc["Form_Policy_Value"], RestartPolicies, "unless-stopped")],
            _loc["Form_Submit"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        IReadOnlyList<BatchOutcome> outcomes = await GuardAsync(
            token => api.UpdateRestartPolicyAsync(SelectedContainerIds, values.Text("policy", "no"), token)).ConfigureAwait(true);
        ReportBatch(_loc["Container_RestartPolicy"], outcomes);
        await LoadContainersAsync().ConfigureAwait(true);
    }

    private async Task ShellIntoContainerAsync()
    {
        if (_api is not { } api || PrimaryContainer is not { } row)
        {
            return;
        }
        IReadOnlyDictionary<string, string>? values = await Form.AskAsync(
            _loc.Format("Form_Exec_Title", row.Model.Name),
            _loc["Container_TerminalHint"],
            [
                PanelForm.Choice("shell", _loc["Form_Exec_Shell"],
                [
                    new("bash", "bash"),
                    new("sh", "sh"),
                    new("ash", "ash"),
                    new("zsh", "zsh")
                ], "bash"),
                PanelForm.Text("user", _loc["Form_Exec_User"], string.Empty, "root"),
                PanelForm.Text("workdir", _loc["Form_Exec_Workdir"], string.Empty, "/app")
            ],
            _loc["Form_Submit"],
            _loc["Common_Cancel"],
            _loc["Form_Preview"],
            v => api.BuildExecCommand(row.Model.Id, v.Text("shell", "bash"), v.Text("user"), v.Text("workdir"))).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        await SendToTerminalAsync(
            api.BuildExecCommand(row.Model.Id, values.Text("shell", "bash"), values.Text("user"), values.Text("workdir")))
            .ConfigureAwait(true);
    }

    /// <summary>单独刷一次头部计数(容器页之外的页签用;容器页是随快照一起回来的)。</summary>
    /// <returns>表示异步操作的任务。</returns>
    private async Task RefreshCountsAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        (int containers, int running, int images, int volumes) = await GuardAsync(api.CountsAsync).ConfigureAwait(true);
        ApplyCounts(new(containers, running, images, volumes));
    }

    /// <summary>可选的重启策略。</summary>
    private static readonly IReadOnlyList<FormChoice> RestartPolicies =
    [
        new("no", "no"),
        new("on-failure", "on-failure"),
        new("always", "always"),
        new("unless-stopped", "unless-stopped")
    ];

    /// <summary>
    /// 把一批目标名摆成确认框里的那一行。
    /// 超过六个就折成"… 与另外 N 个" —— 确认框不该因为选了四十个容器而顶出屏幕,
    /// 但**必须**把前几个的名字写出来:只说"3 个容器"的确认框等于没确认。
    /// </summary>
    /// <param name="names">目标名。</param>
    /// <returns>一行文本。</returns>
    private string DescribeTargets(IEnumerable<string> names)
    {
        List<string> list = [.. names];
        if (list.Count <= 6)
        {
            return string.Join(", ", list);
        }
        return $"{string.Join(", ", list.Take(6))} … (+{list.Count - 6})";
    }
}
