using System.Collections.ObjectModel;
using System.Text;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    private IReadOnlyList<ComposeProjectItem> _allComposeProjects = [];
    private ComposeRow? _selectedCompose;
    private bool _composeAvailable;

    /// <summary>当前显示的 compose 项目行。</summary>
    public ObservableCollection<ComposeRow> ComposeProjects { get; } = [];

    /// <summary>项目列表非空。</summary>
    public bool HasComposeProjects => ComposeProjects.Count > 0;

    /// <summary>远端有可用的 compose。</summary>
    public bool IsComposeAvailable
    {
        get => _composeAvailable;
        private set => SetProperty(ref _composeAvailable, value);
    }

    /// <summary>当前选中的项目。</summary>
    public ComposeRow? SelectedCompose
    {
        get => _selectedCompose;
        set
        {
            if (!SetProperty(ref _selectedCompose, value))
            {
                return;
            }
            RaiseComposeCommandStates();
            _ = LoadDrawerAsync(false);
        }
    }

    /// <summary>up -d。</summary>
    public AsyncCommand ComposeUpCommand { get; private set; } = null!;

    /// <summary>down。</summary>
    public AsyncCommand ComposeDownCommand { get; private set; } = null!;

    /// <summary>restart。</summary>
    public AsyncCommand ComposeRestartCommand { get; private set; } = null!;

    /// <summary>stop。</summary>
    public AsyncCommand ComposeStopCommand { get; private set; } = null!;

    /// <summary>start。</summary>
    public AsyncCommand ComposeStartCommand { get; private set; } = null!;

    /// <summary>pull。</summary>
    public AsyncCommand ComposePullCommand { get; private set; } = null!;

    /// <summary>build。</summary>
    public AsyncCommand ComposeBuildCommand { get; private set; } = null!;

    /// <summary>打开一个 compose 文件(把它当成一个项目来操作)。</summary>
    public AsyncCommand ComposeOpenFileCommand { get; private set; } = null!;

    /// <summary>编辑项目的 compose 文件。</summary>
    public AsyncCommand ComposeEditCommand { get; private set; } = null!;

    private void BuildComposeCommands()
    {
        ComposeUpCommand = new(() => ComposeActionAsync("up -d --remove-orphans", _loc["Compose_Up"]), HasComposeTarget);
        ComposeDownCommand = new(ComposeDownAsync, HasComposeTarget);
        ComposeRestartCommand = new(() => ComposeActionAsync("restart", _loc["Compose_Restart"]), HasComposeTarget);
        ComposeStopCommand = new(() => ComposeActionAsync("stop", _loc["Compose_Stop"]), HasComposeTarget);
        ComposeStartCommand = new(() => ComposeActionAsync("start", _loc["Compose_Start"]), HasComposeTarget);
        ComposePullCommand = new(() => ComposeActionAsync("pull", _loc["Compose_Pull"]), HasComposeTarget);
        ComposeBuildCommand = new(() => ComposeActionAsync("build", _loc["Compose_Build"]), HasComposeTarget);
        ComposeOpenFileCommand = new(OpenComposeFileAsync, () => IsEngineReady);
        ComposeEditCommand = new(() => ShowDrawerAsync(DrawerTab.File), () => HasComposeTarget() && ComposeConfigFile.Length > 0);
    }

    private bool HasComposeTarget() => IsEngineReady && IsComposeAvailable && SelectedCompose is not null;

    private void RaiseComposeCommandStates()
    {
        ComposeUpCommand.RaiseCanExecuteChanged();
        ComposeDownCommand.RaiseCanExecuteChanged();
        ComposeRestartCommand.RaiseCanExecuteChanged();
        ComposeStopCommand.RaiseCanExecuteChanged();
        ComposeStartCommand.RaiseCanExecuteChanged();
        ComposePullCommand.RaiseCanExecuteChanged();
        ComposeBuildCommand.RaiseCanExecuteChanged();
        ComposeEditCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(SelectionSummary));
    }

    private string ComposeProjectName => SelectedCompose?.Model.Name ?? string.Empty;

    private string ComposeConfigFile => SelectedCompose?.Model.PrimaryConfigFile ?? string.Empty;

    private async Task LoadComposeAsync()
    {
        if (_api is not { } api)
        {
            return;
        }
        IsComposeAvailable = api.Engine.Probe.HasCompose;
        if (!IsComposeAvailable)
        {
            ComposeProjects.Clear();
            _allComposeProjects = [];
            RaisePropertyChanged(nameof(HasComposeProjects));
            return;
        }
        (IReadOnlyList<ComposeProjectItem> items, DockerResult result) =
            await GuardAsync(token => api.ListComposeProjectsAsync(true, token)).ConfigureAwait(true);
        if (!result.Ok && items.Count == 0)
        {
            Status = _loc.Format("Status_Failed", _loc["Tab_Compose"], FirstLine(result.FailureText));
        }
        // 手工打开的文件不在 `compose ls` 里(那个项目可能从没起过),但用户刚打开它就不见了
        // 是最让人恼火的一种"刷新"。把它并进来。
        List<ComposeProjectItem> merged = [.. items];
        foreach (ComposeProjectItem manual in _manualProjects)
        {
            if (!merged.Any(p => string.Equals(p.Name, manual.Name, StringComparison.Ordinal)))
            {
                merged.Add(manual);
            }
        }
        _allComposeProjects = [.. merged.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)];
        PublishCompose();
    }

    private readonly List<ComposeProjectItem> _manualProjects = [];

    private void PublishCompose()
    {
        List<ComposeProjectItem> visible = [];
        foreach (ComposeProjectItem item in _allComposeProjects)
        {
            if (Matches(item.Name, item.Status, item.ConfigFiles))
            {
                visible.Add(item);
            }
        }
        string? keep = SelectedCompose?.Key;
        RowSync.Apply(ComposeProjects, visible, static p => p.Name, static p => new ComposeRow(p));
        RaisePropertyChanged(nameof(HasComposeProjects));
        if (keep is not null && ComposeProjects.FirstOrDefault(r => r.Key == keep) is { } restored)
        {
            SelectedCompose = restored;
        }
        else if (SelectedCompose is not null && !ComposeProjects.Contains(SelectedCompose))
        {
            SelectedCompose = null;
        }
    }

    private async Task ComposeActionAsync(string arguments, string label)
    {
        if (_api is not { } api || SelectedCompose is null)
        {
            return;
        }
        Status = _loc.Format("Status_Working", label);
        DockerResult result = await GuardAsync(
            token => api.ComposeAsync(ComposeProjectName, ComposeConfigFile, arguments, null, token)).ConfigureAwait(true);
        ReportResult(label, result);
        // compose 的输出是这个面板里最值得看的一段(哪个服务重建了、哪个健康检查没过)。
        ShowDrawerText(DrawerTab.Output,
            $"$ {api.BuildComposeCommand(ComposeProjectName, ComposeConfigFile, arguments)}\n{result.Output}");
        await LoadComposeAsync().ConfigureAwait(true);
    }

    private async Task ComposeDownAsync()
    {
        if (_api is not { } api || SelectedCompose is null)
        {
            return;
        }
        ConfirmAnswer answer = await Confirm.AskAsync(
            _loc.Format("Confirm_ComposeDown", ComposeProjectName),
            _loc["Confirm_ComposeDownBody"],
            api.BuildComposeCommand(ComposeProjectName, ComposeConfigFile, "down"),
            _loc["Compose_Down"],
            _loc["Common_Cancel"],
            true,
            optionLabel: _loc["Confirm_ComposeDownVolumes"]).ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        // 勾了"连卷一起删"就是在删数据 —— 再要一次手打确认,和删卷同档。
        if (answer.Option)
        {
            ConfirmAnswer second = await Confirm.AskAsync(
                _loc.Format("Confirm_ComposeDown", ComposeProjectName),
                _loc["Confirm_RemoveVolumesBody"],
                api.BuildComposeCommand(ComposeProjectName, ComposeConfigFile, "down -v"),
                _loc["Compose_Down"],
                _loc["Common_Cancel"],
                true,
                ComposeProjectName,
                _loc.Format("Confirm_Type", ComposeProjectName)).ConfigureAwait(true);
            if (!second.Confirmed)
            {
                Status = _loc["Status_Cancelled"];
                return;
            }
        }
        await ComposeActionAsync(answer.Option ? "down -v --remove-orphans" : "down --remove-orphans", _loc["Compose_Down"])
            .ConfigureAwait(true);
    }

    private async Task OpenComposeFileAsync()
    {
        if (_api is null)
        {
            return;
        }
        IReadOnlyDictionary<string, string>? values = await Form.AskAsync(
            _loc["Form_ComposeFile_Title"],
            _loc["Compose_Hint"],
            [
                PanelForm.Text("path", _loc["Form_ComposeFile_Path"], string.Empty, "/srv/app/docker-compose.yml"),
                PanelForm.Text("project", _loc["Form_ComposeFile_Project"], string.Empty, "app", _loc["Form_ComposeFile_ProjectHint"])
            ],
            _loc["Form_Submit"],
            _loc["Common_Cancel"]).ConfigureAwait(true);
        if (values is null)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        string path = values.Text("path");
        if (path.Length == 0)
        {
            return;
        }
        string project = values.Text("project");
        if (project.Length == 0)
        {
            // compose 自己就是按目录名推的,这里跟着推一遍,免得项目行显示成空白。
            string directory = DockerApi.ParentDirectory(path);
            int slash = directory.LastIndexOf('/');
            project = slash >= 0 && slash + 1 < directory.Length ? directory[(slash + 1)..] : directory.Trim('/');
        }
        ComposeProjectItem manual = new() { Name = project, Status = string.Empty, ConfigFiles = path };
        _manualProjects.RemoveAll(p => string.Equals(p.Name, project, StringComparison.Ordinal));
        _manualProjects.Add(manual);
        ActiveTab = DockerTab.Compose;
        await LoadComposeAsync().ConfigureAwait(true);
        SelectedCompose = ComposeProjects.FirstOrDefault(r => r.Key == project);
        Status = _loc.Format("Status_FileRead", path);
    }

    /// <summary>读远端 compose 文件的正文(编辑用)。</summary>
    /// <returns>文件正文;读不到时是一句错误说明。</returns>
    private async Task<string> ReadComposeFileAsync()
    {
        if (SelectedSession is not { } session || ComposeConfigFile.Length == 0)
        {
            return string.Empty;
        }
        try
        {
            // compose 文件是小文本;走 SFTP 读比 `cat` 干净 —— 不经 shell,也就不会被 shell 的
            // 编码/行尾处理动过手脚。
            byte[] bytes = await _context.RemoteFs
                                         .ReadAllBytesAsync(session.SessionId, ComposeConfigFile, 2 * 1024 * 1024, _lifetime.Token)
                                         .ConfigureAwait(true);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            return $"# {ex.Message}";
        }
    }

    /// <summary>把抽屉里的正文写回远端 compose 文件。</summary>
    /// <returns>表示异步操作的任务。</returns>
    private async Task SaveComposeFileAsync()
    {
        if (SelectedSession is not { } session || ComposeConfigFile.Length == 0)
        {
            return;
        }
        ConfirmAnswer answer = await Confirm.AskAsync(
            _loc.Format("Confirm_SaveComposeFile", ComposeConfigFile),
            _loc["Confirm_SaveComposeFileBody"],
            ComposeConfigFile,
            _loc["Drawer_Save"],
            _loc["Common_Cancel"],
            true,
            "save",
            _loc.Format("Confirm_Type", "save")).ConfigureAwait(true);
        if (!answer.Confirmed)
        {
            Status = _loc["Status_Cancelled"];
            return;
        }
        try
        {
            // 结尾补一个换行:YAML 解析器不在意,但 `git diff` 会为"\ No newline at end of file"
            // 记一笔,而这个文件多半是在版本库里的。
            string text = DrawerText.EndsWith('\n') ? DrawerText : DrawerText + "\n";
            await _context.RemoteFs
                          .WriteAllBytesAsync(session.SessionId, ComposeConfigFile, Encoding.UTF8.GetBytes(text), _lifetime.Token)
                          .ConfigureAwait(true);
            Status = _loc.Format("Status_Saved", ComposeConfigFile);
        }
        catch (Exception ex)
        {
            Status = _loc.Format("Status_Failed", _loc["Drawer_Save"], ex.Message);
        }
    }
}
