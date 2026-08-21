using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    private IReadOnlyList<VolumeItem> _allVolumes = [];
    private IReadOnlyList<VolumeRow> _selectedVolumes = [];
    private IReadOnlyList<NetworkItem> _allNetworks = [];
    private IReadOnlyList<NetworkRow> _selectedNetworks = [];

    /// <summary>当前显示的卷行。</summary>
    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    /// <summary>当前显示的网络行。</summary>
    public ObservableCollection<NetworkRow> Networks { get; } = [];

    /// <summary>卷列表非空。</summary>
    public bool HasVolumes => Volumes.Count > 0;

    /// <summary>网络列表非空。</summary>
    public bool HasNetworks => Networks.Count > 0;

    /// <summary>当前选中的卷行。</summary>
    public IReadOnlyList<VolumeRow> SelectedVolumes => _selectedVolumes;

    /// <summary>当前选中的网络行。</summary>
    public IReadOnlyList<NetworkRow> SelectedNetworks => _selectedNetworks;

    /// <summary>选中的第一个卷。</summary>
    public VolumeRow? PrimaryVolume => _selectedVolumes.Count > 0 ? _selectedVolumes[0] : null;

    /// <summary>选中的第一张网络。</summary>
    public NetworkRow? PrimaryNetwork => _selectedNetworks.Count > 0 ? _selectedNetworks[0] : null;

    /// <summary>新建卷。</summary>
    public AsyncCommand CreateVolumeCommand { get; private set; } = null!;

    /// <summary>删卷。</summary>
    public AsyncCommand RemoveVolumesCommand { get; private set; } = null!;

    /// <summary>清理未使用的卷。</summary>
    public AsyncCommand PruneVolumesCommand { get; private set; } = null!;

    /// <summary>新建网络。</summary>
    public AsyncCommand CreateNetworkCommand { get; private set; } = null!;

    /// <summary>删网络。</summary>
    public AsyncCommand RemoveNetworksCommand { get; private set; } = null!;

    /// <summary>清理未使用的网络。</summary>
    public AsyncCommand PruneNetworksCommand { get; private set; } = null!;

    /// <summary>把容器接进网络。</summary>
    public AsyncCommand ConnectNetworkCommand { get; private set; } = null!;

    /// <summary>把容器从网络摘掉。</summary>
    public AsyncCommand DisconnectNetworkCommand { get; private set; } = null!;

    /// <summary>视图在卷选中项变化时调这个。</summary>
    /// <param name="rows">当前选中的行。</param>
    public void SetVolumeSelection(IReadOnlyList<VolumeRow> rows)
    {
        _selectedVolumes = rows;
        RaisePropertyChanged(nameof(SelectedVolumes));
        RaisePropertyChanged(nameof(PrimaryVolume));
        RaisePropertyChanged(nameof(SelectionSummary));
        RemoveVolumesCommand.RaiseCanExecuteChanged();
        _ = LoadDrawerAsync(false);
    }

    /// <summary>视图在网络选中项变化时调这个。</summary>
    /// <param name="rows">当前选中的行。</param>
    public void SetNetworkSelection(IReadOnlyList<NetworkRow> rows)
    {
        _selectedNetworks = rows;
        RaisePropertyChanged(nameof(SelectedNetworks));
        RaisePropertyChanged(nameof(PrimaryNetwork));
        RaisePropertyChanged(nameof(SelectionSummary));
        RemoveNetworksCommand.RaiseCanExecuteChanged();
        ConnectNetworkCommand.RaiseCanExecuteChanged();
        DisconnectNetworkCommand.RaiseCanExecuteChanged();
        _ = LoadDrawerAsync(false);
    }

    private void BuildVolumeCommands()
    {
        CreateVolumeCommand = new(CreateVolumeAsync, () => IsEngineReady);
        RemoveVolumesCommand = new(RemoveVolumesAsync, () => IsEngineReady && _selectedVolumes.Count > 0);
        PruneVolumesCommand = new(() => PruneAsync(PruneKind.Volumes, false, false, _loc["Prune_Volumes"]), () => IsEngineReady);
    }

    private void BuildNetworkCommands()
    {
        CreateNetworkCommand = new(CreateNetworkAsync, () => IsEngineReady);
        // 内置的 bridge / host / none 删不掉:与其让用户撞一条 docker 的错误,不如按钮就不给点。
        RemoveNetworksCommand = new(RemoveNetworksAsync,
            () => IsEngineReady && _selectedNetworks.Count > 0 && _selectedNetworks.All(static r => !r.Model.IsBuiltIn));
        PruneNetworksCommand = new(() => PruneAsync(PruneKind.Networks, false, false, _loc["Prune_Networks"]), () => IsEngineReady);
        ConnectNetworkCommand = new(ConnectNetworkAsync, () => IsEngineReady && _selectedNetworks.Count == 1);
        DisconnectNetworkCommand = new(DisconnectNetworkAsync, () => IsEngineReady && _selectedNetworks.Count == 1);
    }

    private async Task LoadVolumesAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        (var items, var result) = await GuardAsync(api.ListVolumesAsync).ConfigureAwait(true);
        if (!result.IsSuccess && items.Count == 0)
        {
            Status = _loc.Format("Status_Failed", _loc["Tab_Volumes"], FirstLine(result.FailureText));
            return;
        }
        _allVolumes = items;
        PublishVolumes();
    }

    private void PublishVolumes()
    {
        List<VolumeItem> visible = [];
        foreach (var item in _allVolumes)
        {
            if (Matches(item.Name, item.Driver, item.Mountpoint, item.ComposeProject))
            {
                visible.Add(item);
            }
        }
        RowSync.Apply(Volumes, visible, static v => v.Name, static v => new VolumeRow(v));
        RaisePropertyChanged(nameof(HasVolumes));
        if (_selectedVolumes.Count > 0)
        {
            IReadOnlyList<VolumeRow> kept = [.. _selectedVolumes.Where(Volumes.Contains)];
            if (kept.Count != _selectedVolumes.Count)
            {
                SetVolumeSelection(kept);
            }
        }
    }

    private async Task LoadNetworksAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        (var items, var result) = await GuardAsync(api.ListNetworksAsync).ConfigureAwait(true);
        if (!result.IsSuccess && items.Count == 0)
        {
            Status = _loc.Format("Status_Failed", _loc["Tab_Networks"], FirstLine(result.FailureText));
            return;
        }
        _allNetworks = items;
        PublishNetworks();
    }

    private void PublishNetworks()
    {
        List<NetworkItem> visible = [];
        foreach (var item in _allNetworks)
        {
            if (Matches(item.Name, item.Driver, item.ShortId, item.Scope, item.ComposeProject))
            {
                visible.Add(item);
            }
        }
        RowSync.Apply(Networks, visible, static n => n.Id, static n => new NetworkRow(n));
        RaisePropertyChanged(nameof(HasNetworks));
        if (_selectedNetworks.Count > 0)
        {
            IReadOnlyList<NetworkRow> kept = [.. _selectedNetworks.Where(Networks.Contains)];
            if (kept.Count != _selectedNetworks.Count)
            {
                SetNetworkSelection(kept);
            }
        }
    }

    private async Task CreateVolumeAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        var values = await Form.AskAsync(
            _loc["Form_Volume_Title"],
            string.Empty,
            [
                PanelForm.Text("name", _loc["Form_Volume_Name"], string.Empty, "app-data"),
                PanelForm.Text("driver", _loc["Form_Volume_Driver"], string.Empty, "local"),
                PanelForm.Multiline("options", _loc["Form_Volume_Options"], string.Empty, "type=nfs\ndevice=:/exported/path"),
                PanelForm.Multiline("labels", _loc["Form_Volume_Labels"], string.Empty, "owner=team-a")
            ],
            _loc["Volume_Create"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var result = await GuardAsync(token => api.CreateVolumeAsync(
            values.Text("name"), values.Text("driver"), values.Lines("options"), values.Lines("labels"), token)).ConfigureAwait(true);
        ReportResult(_loc["Volume_Create"], result);
        await LoadVolumesAsync().ConfigureAwait(true);
    }

    private async Task RemoveVolumesAsync()
    {
        if (_api is not { } api || _selectedVolumes.Count == 0)
        {
            return;
        }
        IReadOnlyList<string> names = [.. _selectedVolumes.Select(static r => r.Model.Name)];
        // 删卷是这个面板里唯一"删完就没了、且删的是数据"的常规动作(prune --volumes 是另一个)。
        // 手打确认串,与删仓库同款护栏。
        var answer = await Confirm.AskAsync(
            _loc.Format("Confirm_RemoveVolumes", names.Count),
            _loc["Confirm_RemoveVolumesBody"],
            DescribeTargets(names),
            _loc["Volume_Remove"],
            _loc["Common_Cancel"],
            true,
            "delete",
            _loc.Format("Confirm_Type", "delete"),
            "force (-f)").ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var outcomes = await GuardAsync(
            token => api.RemoveVolumesAsync(names, answer.Option, token)).ConfigureAwait(true);
        ReportBatch(_loc["Volume_Remove"], outcomes);
        SetVolumeSelection([]);
        await LoadVolumesAsync().ConfigureAwait(true);
    }

    private async Task CreateNetworkAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        var values = await Form.AskAsync(
            _loc["Form_Network_Title"],
            string.Empty,
            [
                PanelForm.Text("name", _loc["Form_Network_Name"], string.Empty, "app-net"),
                PanelForm.Choice("driver", _loc["Form_Network_Driver"],
                [
                    new("bridge", "bridge"),
                    new("overlay", "overlay"),
                    new("macvlan", "macvlan"),
                    new("ipvlan", "ipvlan"),
                    new("none", "none")
                ], "bridge"),
                PanelForm.Text("subnet", _loc["Form_Network_Subnet"], string.Empty, "172.28.0.0/16"),
                PanelForm.Text("gateway", _loc["Form_Network_Gateway"], string.Empty, "172.28.0.1"),
                PanelForm.Boolean("internal", _loc["Form_Network_Internal"]),
                PanelForm.Boolean("ipv6", _loc["Form_Network_IPv6"])
            ],
            _loc["Network_Create"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var name = values.Text("name");
        if (name.Length == 0)
        {
            return;
        }
        var result = await GuardAsync(token => api.CreateNetworkAsync(
            name, values.Text("driver"), values.Text("subnet"), values.Text("gateway"),
            values.Flag("internal"), values.Flag("ipv6"), token)).ConfigureAwait(true);
        ReportResult(_loc["Network_Create"], result);
        await LoadNetworksAsync().ConfigureAwait(true);
    }

    private async Task RemoveNetworksAsync()
    {
        if (_api is not { } api || _selectedNetworks.Count == 0)
        {
            return;
        }
        IReadOnlyList<string> names = [.. _selectedNetworks.Select(static r => r.Model.Name)];
        var answer = await Confirm.AskAsync(
            _loc.Format("Confirm_RemoveNetworks", names.Count),
            _loc["Confirm_RemoveNetworksBody"],
            DescribeTargets(names),
            _loc["Network_Remove"],
            _loc["Common_Cancel"],
            true).ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var outcomes = await GuardAsync(token => api.RemoveNetworksAsync(names, token)).ConfigureAwait(true);
        ReportBatch(_loc["Network_Remove"], outcomes);
        SetNetworkSelection([]);
        await LoadNetworksAsync().ConfigureAwait(true);
    }

    private async Task ConnectNetworkAsync()
    {
        if (_api is not { } api || PrimaryNetwork is not { } row)
        {
            return;
        }
        var values = await Form.AskAsync(
            _loc.Format("Form_Connect_Title", row.Model.Name),
            string.Empty,
            [
                PanelForm.Choice("container", _loc["Form_Connect_Container"], ContainerChoices()),
                PanelForm.Text("alias", _loc["Form_Connect_Alias"])
            ],
            _loc["Form_Submit"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var container = values.Text("container");
        if (container.Length == 0)
        {
            return;
        }
        var result = await GuardAsync(
            token => api.ConnectNetworkAsync(row.Model.Name, container, values.Text("alias"), token)).ConfigureAwait(true);
        ReportResult(_loc["Network_Connect"], result);
    }

    private async Task DisconnectNetworkAsync()
    {
        if (_api is not { } api || PrimaryNetwork is not { } row)
        {
            return;
        }
        var values = await Form.AskAsync(
            _loc.Format("Form_Disconnect_Title", row.Model.Name),
            string.Empty,
            [PanelForm.Choice("container", _loc["Form_Connect_Container"], ContainerChoices())],
            _loc["Form_Submit"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var container = values.Text("container");
        if (container.Length == 0)
        {
            return;
        }
        var result = await GuardAsync(token => api.DisconnectNetworkAsync(row.Model.Name, container, token)).ConfigureAwait(true);
        ReportResult(_loc["Network_Disconnect"], result);
    }

    /// <summary>
    /// 容器下拉的选项。
    /// 用**已经列出来的**容器,而不是再跑一次 <c>docker ps</c>:表单只是要一个名字,
    /// 为它多一次往返不值当;真有新容器,用户刷新一下就有了。
    /// </summary>
    /// <returns>下拉选项。</returns>
    private IReadOnlyList<FormChoice> ContainerChoices() =>
        _allContainers.Count > 0
            ? [.. _allContainers.Select(static c => new FormChoice(c.Name, $"{c.Name} · {c.Image}"))]
            : [new(string.Empty, "—")];
}
