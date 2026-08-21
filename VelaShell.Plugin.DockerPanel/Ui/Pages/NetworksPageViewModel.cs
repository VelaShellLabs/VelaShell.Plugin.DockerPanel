using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>网络详情里已接入的一个容器。</summary>
/// <param name="Id">容器 id。</param>
/// <param name="Name">容器名。</param>
/// <param name="Address">IPv4(带掩码)。</param>
public readonly record struct AttachedContainer(string Id, string Name, string Address);

/// <summary>网络页。</summary>
public sealed class NetworksPageViewModel : PageViewModel
{
    private readonly List<NetworkRow> _all = [];
    private string _search = "";
    private NetworkRow? _selected;
    private bool _swarmActive;

    /// <summary>建网络页。</summary>
    public NetworksPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        SelectCommand = new RelayCommand(p => SelectAsync(p as NetworkRow));
        CreateCommand = new RelayCommand(_ => CreateAsync());
        RemoveCommand = new RelayCommand(p => p is NetworkRow row ? RemoveAsync(row) : Task.CompletedTask);
        PruneCommand = new RelayCommand(_ => PruneAsync());
        ConnectCommand = new RelayCommand(p => p is NetworkRow row ? ConnectAsync(row) : Task.CompletedTask);
        DisconnectCommand = new RelayCommand(p => p is AttachedContainer attached
            ? DisconnectAsync(attached)
            : Task.CompletedTask);
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Networks;

    /// <inheritdoc />
    public override string Title => "网络";

    /// <summary>过滤后的行。</summary>
    public KeyedCollection<NetworkRow> View { get; } = new(r => r.Id);

    /// <summary>搜索词。</summary>
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

    /// <summary>总数。</summary>
    public int TotalCount => _all.Count;

    /// <summary>自定义网络数。</summary>
    public int CustomCount => _all.Count(r => !r.IsPredefined);

    /// <summary>内置网络数。</summary>
    public int PredefinedCount => _all.Count(r => r.IsPredefined);

    /// <summary>未接入任何容器的自定义网络数。</summary>
    public int UnusedCount => _all.Count(r => !r.IsPredefined && r.AttachedCount == 0);

    /// <summary>列表空了。</summary>
    public bool IsEmpty => LoadedOnce && _all.Count == 0;

    /// <summary>当前选中的网络。</summary>
    public NetworkRow? Selected
    {
        get => _selected;
        private set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(CanRemove), nameof(RemoveHint));
            }
        }
    }

    /// <summary>有选中。</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>选中网络的接入容器。</summary>
    public ObservableCollection<AttachedContainer> Attached { get; } = [];

    /// <summary>选中网络的 IPAM 明细。</summary>
    public ObservableCollection<DetailField> Ipam { get; } = [];

    /// <summary>选中网络的基本信息。</summary>
    public ObservableCollection<DetailField> Basics { get; } = [];

    /// <summary>能不能删。</summary>
    public bool CanRemove => Selected is { IsPredefined: false, AttachedCount: 0 };

    /// <summary>不能删时的原因。</summary>
    public string RemoveHint => Selected switch
    {
        null => "",
        { IsPredefined: true } => "Docker 内置的 bridge / host / none 删不掉。",
        { AttachedCount: > 0 } row => $"仍有 {row.AttachedCount} 个容器接入,必须先摘除或停止它们。",
        _ => "删除网络本身不会丢数据。"
    };

    /// <summary>选中一行。</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>新建网络。</summary>
    public RelayCommand CreateCommand { get; }

    /// <summary>删除网络。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>清理未使用网络。</summary>
    public RelayCommand PruneCommand { get; }

    /// <summary>接入容器。</summary>
    public RelayCommand ConnectCommand { get; }

    /// <summary>摘除容器。</summary>
    public RelayCommand DisconnectCommand { get; }

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

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
            NetworkSummary[] networks = await client.ListNetworksAsync(cancellationToken).ConfigureAwait(true);
            List<NetworkRow> incoming =
            [
                .. networks
                    .OrderBy(n => n.IsPredefined)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new NetworkRow(n))
            ];
            Dictionary<string, NetworkRow> previous = _all.ToDictionary(r => r.Id);
            _all.Clear();
            foreach (NetworkRow row in incoming)
            {
                if (previous.TryGetValue(row.Id, out NetworkRow? existing))
                {
                    existing.Update(row);
                    _all.Add(existing);
                }
                else
                {
                    _all.Add(row);
                }
            }
            LoadedOnce = true;
            ApplyView();
            if (Selected is { } selected)
            {
                NetworkRow? still = _all.FirstOrDefault(r => r.Id == selected.Id);
                Selected = still;
                if (still is not null)
                {
                    await LoadDetailAsync(still, cancellationToken).ConfigureAwait(true);
                }
            }
            OnPropertiesChanged(nameof(TotalCount), nameof(CustomCount), nameof(PredefinedCount), nameof(UnusedCount));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _all.Clear();
        View.Clear();
        Attached.Clear();
        Ipam.Clear();
        Basics.Clear();
        Selected = null;
        LoadedOnce = false;
        OnPropertiesChanged(nameof(TotalCount), nameof(CustomCount), nameof(PredefinedCount), nameof(IsEmpty));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type == "network";

    private void ApplyView()
    {
        string needle = _search.Trim();
        IEnumerable<NetworkRow> filtered = needle.Length == 0
            ? _all
            : _all.Where(r =>
                r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.Subnet.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.Driver.Contains(needle, StringComparison.OrdinalIgnoreCase));
        View.Merge([.. filtered], (_, _) => { });
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task SelectAsync(NetworkRow? row)
    {
        Selected = row;
        if (row is not null)
        {
            await LoadDetailAsync(row, Shell.Lifetime).ConfigureAwait(true);
        }
    }

    private async Task LoadDetailAsync(NetworkRow row, CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        try
        {
            NetworkSummary detail = await client.InspectNetworkAsync(row.Id, cancellationToken).ConfigureAwait(true);
            Attached.Clear();
            foreach ((string id, NetworkContainer container) in detail.Containers ?? [])
            {
                Attached.Add(new(id, container.Name ?? Humanize.ShortId(id), container.IPv4Address ?? "—"));
            }
            Ipam.Clear();
            Ipam.Add(new("子网", detail.FirstSubnet ?? "(自动)", RowTone.Ok));
            Ipam.Add(new("网关", detail.FirstGateway ?? "(自动)", RowTone.Ok));
            Ipam.Add(new("IPAM 驱动", detail.IPAM?.Driver ?? "default"));
            Basics.Clear();
            Basics.Add(new("网络 ID", Humanize.ShortId(detail.Id)));
            Basics.Add(new("驱动", detail.Driver ?? "—"));
            Basics.Add(new("作用域", detail.Scope ?? "—"));
            Basics.Add(new("创建于", Humanize.LocalTime(detail.Created)));
            Basics.Add(new("internal", detail.Internal ? "是 · 不通外网" : "否 · 可访问外网"));
            Basics.Add(new("attachable", detail.Attachable ? "是" : "否"));
            Basics.Add(new("IPv6", detail.EnableIPv6 ? "已启用" : "关闭"));
            OnPropertiesChanged(nameof(CanRemove), nameof(RemoveHint));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("读取网络详情", ex);
        }
    }

    private async Task CreateAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        try
        {
            SystemInfo info = await client.InfoAsync(Shell.Lifetime).ConfigureAwait(true);
            _swarmActive = info.Swarm?.LocalNodeState == "active";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _swarmActive = false;
        }
        var form = new CreateNetworkForm(_swarmActive);
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            CreateNetworkResponse created = await client.CreateNetworkAsync(form.Name, form.Driver, form.Subnet,
                form.Gateway, form.Internal, form.Attachable, form.EnableIPv6, Shell.Lifetime).ConfigureAwait(true);
            if (created.Warning is { Length: > 0 } warning)
            {
                Shell.Feedback.Notify(FeedbackKind.Warning, "daemon 有话说", warning);
            }
            else
            {
                Shell.Feedback.Status(FeedbackKind.Success, $"已新建网络 {form.Name}");
            }
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("新建网络", ex);
        }
    }

    private async Task RemoveAsync(NetworkRow row)
    {
        if (Client is not { } client || row.IsPredefined)
        {
            return;
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"删除网络 {row.Name}?",
            Icon = "Icon.trash-2",
            HostName = "",
            ConfirmLabel = "删除网络",
            Commands = [$"DELETE /networks/{Humanize.ShortId(row.Id)}"],
            CommandNote = $"等价于  docker network rm {row.Name}",
            Consequences =
            [
                new(1, "删除网络本身不会丢数据。"),
                new(row.AttachedCount > 0 ? 3 : 0,
                    row.AttachedCount > 0
                        ? $"仍有 {row.AttachedCount} 个容器接入 —— daemon 会拒绝,先摘除或停止它们。"
                        : "当前没有容器接入。"),
                new(0, "compose 项目的默认网络删掉后,下次 up 会自动重建。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await client.RemoveNetworkAsync(row.Id, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, $"已删除网络 {row.Name}");
            Selected = null;
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("删除网络", ex);
        }
    }

    private async Task PruneAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = "清理未使用的网络?",
            Icon = "Docker.broom",
            HostName = "",
            ConfirmLabel = "开始清理",
            ConfirmIcon = "Docker.broom",
            Commands = ["POST /networks/prune"],
            CommandNote = "等价于  docker network prune",
            Consequences =
            [
                new(1, "不丢数据 —— 网络只是配置。"),
                new(0, $"当前有 {UnusedCount} 个自定义网络没有容器接入。"),
                new(2, "已停止的 compose 项目,它的网络也在这个名单里;下次 up 会自动重建。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            PruneReport report = await client.PruneNetworksAsync(Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "清理完成", $"删除 {report.DeletedCount} 个网络");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("清理网络", ex);
        }
    }

    private async Task ConnectAsync(NetworkRow row)
    {
        if (Client is not { } client)
        {
            return;
        }
        ContainerSummary[] containers = await client.ListContainersAsync(true, Shell.Lifetime).ConfigureAwait(true);
        HashSet<string> already = [.. Attached.Select(a => a.Id)];
        var form = new ConnectNetworkForm(
            $"{row.Name} · {row.Driver} · {row.Subnet}",
            containers.Select(c => (
                c.Id,
                c.Name,
                Meta: already.Contains(c.Id) ? "已在这个网络里" : c.State == "running" ? "运行中" : "已停止 · 接入后下次启动生效",
                Enabled: !already.Contains(c.Id),
                Reason: already.Contains(c.Id) ? "已在这个网络里" : "")));
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        BatchResult result = await BatchRunner.RunAsync(
            [.. form.SelectedIds.Select(id => (Target: id, Name: containers.First(c => c.Id == id).Name))],
            (id, ct) => client.ConnectNetworkAsync(row.Id, id, form.Aliases.Length > 0 ? form.Aliases : null, ct),
            null, Shell.Lifetime).ConfigureAwait(true);
        Shell.Feedback.ReportBatch("接入", result, Shell.CurrentPage == PanelPage.Networks);
        await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
    }

    private async Task DisconnectAsync(AttachedContainer attached)
    {
        if (Client is not { } client || Selected is not { } row)
        {
            return;
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"把 {attached.Name} 从 {row.Name} 上摘掉?",
            Icon = "Icon.unplug",
            HostName = "",
            ConfirmLabel = "摘除",
            ConfirmIcon = "Icon.unplug",
            Commands = [$"POST /networks/{Humanize.ShortId(row.Id)}/disconnect"],
            CommandNote = $"等价于  docker network disconnect {row.Name} {attached.Name}",
            Consequences =
            [
                new(2, "容器会立刻失去这个网络上的连通性 —— 正在进行的连接会断。"),
                new(0, "不重启容器,也不丢数据。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await client.DisconnectNetworkAsync(row.Id, attached.Id, false, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, $"已把 {attached.Name} 从 {row.Name} 摘掉");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("摘除容器", ex);
        }
    }
}
