using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    private IReadOnlyList<ImageItem> _allImages = [];
    private IReadOnlyList<ImageRow> _selectedImages = [];
    private bool _showAllImages;

    /// <summary>当前显示的镜像行(已过滤)。</summary>
    public ObservableCollection<ImageRow> Images { get; } = [];

    /// <summary>是否连中间层一起列。</summary>
    public bool ShowAllImages
    {
        get => _showAllImages;
        set
        {
            if (SetProperty(ref _showAllImages, value))
            {
                _ = SaveSettingAsync("showAllImages", value);
                _ = RefreshActiveAsync(true);
            }
        }
    }

    /// <summary>当前选中的镜像行。</summary>
    public IReadOnlyList<ImageRow> SelectedImages => _selectedImages;

    /// <summary>选中的第一行。</summary>
    public ImageRow? PrimaryImage => _selectedImages.Count > 0 ? _selectedImages[0] : null;

    /// <summary>镜像列表非空。</summary>
    public bool HasImages => Images.Count > 0;

    /// <summary>拉镜像。</summary>
    public AsyncCommand PullImageCommand { get; private set; } = null!;

    /// <summary>推镜像。</summary>
    public AsyncCommand PushImageCommand { get; private set; } = null!;

    /// <summary>删镜像。</summary>
    public AsyncCommand RemoveImagesCommand { get; private set; } = null!;

    /// <summary>打标签。</summary>
    public AsyncCommand TagImageCommand { get; private set; } = null!;

    /// <summary>用镜像跑一个容器。</summary>
    public AsyncCommand RunImageCommand { get; private set; } = null!;

    /// <summary>清理悬空镜像。</summary>
    public AsyncCommand PruneDanglingCommand { get; private set; } = null!;

    /// <summary>复制镜像 id。</summary>
    public AsyncCommand CopyImageIdCommand { get; private set; } = null!;

    /// <summary>视图在选中项变化时调这个。</summary>
    /// <param name="rows">当前选中的行。</param>
    public void SetImageSelection(IReadOnlyList<ImageRow> rows)
    {
        _selectedImages = rows;
        RaisePropertyChanged(nameof(SelectedImages));
        RaisePropertyChanged(nameof(PrimaryImage));
        RaisePropertyChanged(nameof(SelectionSummary));
        PushImageCommand.RaiseCanExecuteChanged();
        RemoveImagesCommand.RaiseCanExecuteChanged();
        TagImageCommand.RaiseCanExecuteChanged();
        RunImageCommand.RaiseCanExecuteChanged();
        CopyImageIdCommand.RaiseCanExecuteChanged();
        _ = LoadDrawerAsync(false);
    }

    private void BuildImageCommands()
    {
        PullImageCommand = new(PullImageAsync, () => IsEngineReady);
        PushImageCommand = new(PushImageAsync, HasSingleImage);
        RemoveImagesCommand = new(RemoveImagesAsync, HasImageSelection);
        TagImageCommand = new(TagImageAsync, HasSingleImage);
        RunImageCommand = new(RunImageAsync, HasSingleImage);
        PruneDanglingCommand = new(() => PruneAsync(PruneKind.Images, false, false, _loc["Prune_Images"]), () => IsEngineReady);
        CopyImageIdCommand = new(() => CopyAsync(PrimaryImage?.Model.ShortId), HasSingleImage);
    }

    private bool HasImageSelection() => IsEngineReady && _selectedImages.Count > 0;

    private bool HasSingleImage() => IsEngineReady && _selectedImages.Count == 1;

    private async Task LoadImagesAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        (var items, var result) =
            await GuardAsync(token => api.ListImagesAsync(ShowAllImages, token)).ConfigureAwait(true);
        if (!result.IsSuccess && items.Count == 0)
        {
            Status = _loc.Format("Status_Failed", _loc["Tab_Images"], FirstLine(result.FailureText));
            return;
        }
        _allImages = items;
        PublishImages();
    }

    private void PublishImages()
    {
        List<ImageItem> visible = [];
        foreach (var item in _allImages)
        {
            if (Matches(item.Repository, item.Tag, item.ShortId, item.Display))
            {
                visible.Add(item);
            }
        }
        RowSync.Apply(Images, visible, static i => $"{i.Id}|{i.Repository}:{i.Tag}", static i => new ImageRow(i));
        RaisePropertyChanged(nameof(HasImages));
        if (_selectedImages.Count > 0)
        {
            IReadOnlyList<ImageRow> kept = [.. _selectedImages.Where(Images.Contains)];
            if (kept.Count != _selectedImages.Count)
            {
                SetImageSelection(kept);
            }
        }
    }

    private async Task PullImageAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        var values = await Form.AskAsync(
            _loc["Form_Pull_Title"],
            string.Empty,
            [
                PanelForm.Text("image", _loc["Form_Pull_Image"], PrimaryImage?.Model.Reference ?? string.Empty, "nginx:1.27-alpine"),
                PanelForm.Text("platform", _loc["Form_Pull_Platform"], string.Empty, "linux/amd64"),
                PanelForm.Boolean("allTags", _loc["Form_Pull_AllTags"])
            ],
            _loc["Image_Pull"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var reference = values.Text("image");
        if (reference.Length == 0)
        {
            return;
        }
        Status = _loc.Format("Status_Working", _loc["Image_Pull"]);
        var result = await GuardAsync(
            token => api.PullImageAsync(reference, values.Flag("allTags"), values.Text("platform"), token)).ConfigureAwait(true);
        ReportResult(_loc["Image_Pull"], result);
        // 拉取的输出值得看(层复用、摘要、警告),摆进抽屉而不是只留状态栏一行。
        ShowDrawerText(DrawerTab.Output, $"$ {_loc["Image_Pull"]} {reference}\n{result.Output}");
        await LoadImagesAsync().ConfigureAwait(true);
    }

    private async Task PushImageAsync()
    {
        if (_api is not { } api || PrimaryImage is not { } row)
        {
            return;
        }
        Status = _loc.Format("Status_Working", _loc["Image_Push"]);
        var result = await GuardAsync(token => api.PushImageAsync(row.Model.Reference, token)).ConfigureAwait(true);
        ReportResult(_loc["Image_Push"], result);
        ShowDrawerText(DrawerTab.Output, $"$ {_loc["Image_Push"]} {row.Model.Reference}\n{result.Output}");
    }

    private async Task RemoveImagesAsync()
    {
        if (_api is not { } api || _selectedImages.Count == 0)
        {
            return;
        }
        IReadOnlyList<string> references = [.. _selectedImages.Select(static r => r.Model.Reference)];
        var answer = await Confirm.AskAsync(
            _loc.Format("Confirm_RemoveImages", references.Count),
            _loc["Confirm_RemoveImagesBody"],
            DescribeTargets(_selectedImages.Select(static r => r.Model.Display)),
            _loc["Image_Remove"],
            _loc["Common_Cancel"],
            true,
            // 强删的勾选项而不是另一个按钮:两个几乎同名的删除按钮是误触之源。
            optionLabel: "force (-f)").ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var outcomes = await GuardAsync(
            token => api.RemoveImagesAsync(references, answer.Option, token)).ConfigureAwait(true);
        ReportBatch(_loc["Image_Remove"], outcomes);
        SetImageSelection([]);
        await LoadImagesAsync().ConfigureAwait(true);
    }

    private async Task TagImageAsync()
    {
        if (_api is not { } api || PrimaryImage is not { } row)
        {
            return;
        }
        var values = await Form.AskAsync(
            _loc.Format("Form_Tag_Title", row.Model.Display),
            string.Empty,
            [PanelForm.Text("target", _loc["Form_Tag_Target"], string.Empty, "registry.example.com/app:1.0")],
            _loc["Form_Submit"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var target = values.Text("target");
        if (target.Length == 0)
        {
            return;
        }
        var result = await GuardAsync(token => api.TagImageAsync(row.Model.Reference, target, token)).ConfigureAwait(true);
        ReportResult(_loc["Image_Tag"], result);
        await LoadImagesAsync().ConfigureAwait(true);
    }

    private async Task RunImageAsync()
    {
        if (_api is not { } api || PrimaryImage is not { } row)
        {
            return;
        }
        var image = row.Model.Reference;
        IReadOnlyList<FormChoice> networks =
        [
            new(string.Empty, "(default)"),
            .. Networks.Select(static n => new FormChoice(n.Model.Name, n.Model.Name))
        ];
        var values = await Form.AskAsync(
            _loc.Format("Form_Run_Title", image),
            string.Empty,
            [
                PanelForm.Text("name", _loc["Form_Run_Name"], string.Empty, "my-service"),
                PanelForm.Multiline("ports", _loc["Form_Run_Ports"], string.Empty, "8080:80\n127.0.0.1:5432:5432/tcp"),
                PanelForm.Multiline("volumes", _loc["Form_Run_Volumes"], string.Empty, "/srv/data:/var/lib/data\nmyvol:/data:ro"),
                PanelForm.Multiline("env", _loc["Form_Run_Env"], string.Empty, "TZ=Asia/Shanghai"),
                PanelForm.Choice("network", _loc["Form_Run_Network"], networks),
                PanelForm.Choice("restart", _loc["Form_Run_Restart"], RestartPolicies, "unless-stopped"),
                PanelForm.Boolean("detach", _loc["Form_Run_Detach"], true),
                PanelForm.Boolean("rm", _loc["Form_Run_Rm"]),
                PanelForm.Text("command", _loc["Form_Run_Command"], string.Empty, "sh -c 'sleep infinity'"),
                PanelForm.Text("extra", _loc["Form_Run_Extra"], string.Empty, "--cpus 1 --memory 512m", _loc["Form_Run_ExtraHint"])
            ],
            _loc["Common_Run"],
            _loc["Common_Cancel"],
            _loc["Form_Preview"],
            v => api.BuildRunCommand(SpecFrom(v, image))).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        var spec = SpecFrom(values, image);
        Status = _loc.Format("Status_Working", _loc["Common_Run"]);
        var result = await GuardAsync(token => api.RunContainerAsync(spec, token)).ConfigureAwait(true);
        ReportResult(_loc["Common_Run"], result);
        ShowDrawerText(DrawerTab.Output, $"$ {api.BuildRunCommand(spec)}\n{result.Output}");
        if (result.IsSuccess)
        {
            ActiveTab = DockerTab.Containers;
            await LoadContainersAsync().ConfigureAwait(true);
        }
    }

    private static RunSpec SpecFrom(IReadOnlyDictionary<string, string> values, string image) => new()
    {
        Image = image,
        Name = values.Text("name"),
        Ports = values.Lines("ports"),
        Volumes = values.Lines("volumes"),
        Environment = values.Lines("env"),
        Network = values.Text("network"),
        RestartPolicy = values.Text("restart", "no"),
        Detach = values.Flag("detach", true),
        RemoveOnExit = values.Flag("rm"),
        Command = values.Text("command"),
        ExtraArgs = values.Text("extra")
    };
}
