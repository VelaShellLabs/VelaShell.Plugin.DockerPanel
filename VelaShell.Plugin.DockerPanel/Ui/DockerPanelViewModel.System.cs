using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    private string _systemVersionText = string.Empty;
    private string _systemInfoText = string.Empty;
    private string _systemDiskText = string.Empty;

    /// <summary><c>docker version</c> 的输出。</summary>
    public string SystemVersionText
    {
        get => _systemVersionText;
        private set => SetProperty(ref _systemVersionText, value);
    }

    /// <summary><c>docker info</c> 的输出。</summary>
    public string SystemInfoText
    {
        get => _systemInfoText;
        private set => SetProperty(ref _systemInfoText, value);
    }

    /// <summary><c>docker system df -v</c> 的输出。</summary>
    public string SystemDiskText
    {
        get => _systemDiskText;
        private set => SetProperty(ref _systemDiskText, value);
    }

    /// <summary>清理已停止的容器。</summary>
    public AsyncCommand PruneContainersCommand { get; private set; } = null!;

    /// <summary>清理悬空镜像。</summary>
    public AsyncCommand PruneImagesCommand { get; private set; } = null!;

    /// <summary>清理全部未使用镜像。</summary>
    public AsyncCommand PruneAllImagesCommand { get; private set; } = null!;

    /// <summary>清理构建缓存。</summary>
    public AsyncCommand PruneBuildCacheCommand { get; private set; } = null!;

    /// <summary>整体回收(不含卷)。</summary>
    public AsyncCommand PruneEverythingCommand { get; private set; } = null!;

    /// <summary>整体回收(含卷)。</summary>
    public AsyncCommand PruneEverythingWithVolumesCommand { get; private set; } = null!;

    private void BuildSystemCommands()
    {
        PruneContainersCommand = new(() => PruneAsync(PruneKind.Containers, false, false, _loc["Prune_Containers"]), () => IsEngineReady);
        PruneImagesCommand = new(() => PruneAsync(PruneKind.Images, false, false, _loc["Prune_Images"]), () => IsEngineReady);
        PruneAllImagesCommand = new(() => PruneAsync(PruneKind.Images, true, false, _loc["Prune_ImagesAll"]), () => IsEngineReady);
        PruneBuildCacheCommand = new(() => PruneAsync(PruneKind.BuildCache, false, false, _loc["Prune_BuildCache"]), () => IsEngineReady);
        PruneEverythingCommand = new(() => PruneAsync(PruneKind.All, true, false, _loc["Prune_All"]), () => IsEngineReady);
        PruneEverythingWithVolumesCommand =
            new(() => PruneAsync(PruneKind.All, true, true, _loc["Prune_AllWithVolumes"]), () => IsEngineReady);
    }

    private async Task LoadSystemAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        var snapshot = await GuardAsync(api.SystemSnapshotAsync).ConfigureAwait(true);
        SystemVersionText = snapshot.Version;
        SystemInfoText = snapshot.Info;
        SystemDiskText = snapshot.DiskUsage;
        await RefreshCountsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 回收空间。
    /// <para>
    /// 每一档都要过确认,而且确认框里摆的是**那条真命令**。prune 是这个面板里最容易造成
    /// "我以为只是清缓存"的动作:<c>image prune -a</c> 会删掉所有没有容器在用的镜像
    /// (包括你昨天刚推上去、今晚才要部署的那个),<c>--volumes</c> 会删数据。
    /// 后者额外要求手打确认串。
    /// </para>
    /// </summary>
    /// <param name="kind">类别。</param>
    /// <param name="allImages">镜像连有标签的一起清。</param>
    /// <param name="withVolumes">连卷一起清。</param>
    /// <param name="label">界面上的名字。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task PruneAsync(PruneKind kind, bool allImages, bool withVolumes, string label)
    {
        if (_api is not { } api)
        {
            return;
        }
        var losesData = withVolumes || kind is PruneKind.Volumes;
        var answer = await Confirm.AskAsync(
            _loc.Format("Confirm_Prune", label),
            losesData ? _loc["Confirm_PruneVolumesBody"] : _loc["Confirm_PruneBody"],
            api.BuildPruneCommand(kind, allImages, withVolumes),
            _loc["System_Prune"],
            _loc["Common_Cancel"],
            true,
            losesData ? "prune" : null,
            losesData ? _loc.Format("Confirm_Type", "prune") : null).ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        Status = _loc.Format("Status_Working", label);
        var result = await GuardAsync(token => api.PruneAsync(kind, allImages, withVolumes, token)).ConfigureAwait(true);
        ReportResult(label, result);
        // prune 的输出末尾是"Total reclaimed space: 3.2GB" —— 那正是用户按下它想知道的数字。
        ShowDrawerText(DrawerTab.Output, $"$ {api.BuildPruneCommand(kind, allImages, withVolumes)}\n{result.Output}");
        await RefreshActiveAsync(true).ConfigureAwait(true);
    }
}
