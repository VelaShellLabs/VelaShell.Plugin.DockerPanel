using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>执行记录里的一行。</summary>
public sealed class OutputLine(string time, string text, bool isError, bool isCommand)
{
    /// <summary>时刻。</summary>
    public string Time { get; } = time;

    /// <summary>文本。</summary>
    public string Text { get; } = text;

    /// <summary>来自标准错误。</summary>
    public bool IsError { get; } = isError;

    /// <summary>是不是那条命令本身。</summary>
    public bool IsCommand { get; } = isCommand;
}

/// <summary>Compose 页。</summary>
public sealed class ComposePageViewModel : PageViewModel
{
    private ComposeProject? _selected;
    private string _yaml = "";
    private string _originalYaml = "";
    private string _error = "";
    private bool _running;
    private bool _composeAvailable = true;

    /// <summary>建 Compose 页。</summary>
    public ComposePageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        SelectCommand = new RelayCommand(p => p is ComposeProject project ? SelectAsync(project) : Task.CompletedTask);
        UpCommand = new RelayCommand(_ => RunAsync("up -d", "启动项目"));
        StopCommand = new RelayCommand(_ => RunAsync("stop", "停止项目"));
        RestartCommand = new RelayCommand(_ => RunAsync("restart", "重启项目"));
        PullCommand = new RelayCommand(_ => RunAsync("pull", "拉取镜像"));
        BuildCommand = new RelayCommand(_ => RunAsync("build", "构建镜像"));
        DownCommand = new RelayCommand(_ => DownAsync(withVolumes: false));
        DownVolumesCommand = new RelayCommand(_ => DownAsync(withVolumes: true));
        ConfigCommand = new RelayCommand(_ => ShowConfigAsync());
        SaveCommand = new RelayCommand(_ => SaveYamlAsync());
        RevertCommand = new RelayCommand(_ => Yaml = _originalYaml);
        ClearOutputCommand = new RelayCommand(_ => Output.Clear());
        OpenByPathCommand = new RelayCommand(_ => OpenByPathAsync());
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Compose;

    /// <inheritdoc />
    public override string Title => "Compose";

    /// <summary>项目。</summary>
    public ObservableCollection<ComposeProject> Projects { get; } = [];

    /// <summary>选中项目的服务。</summary>
    public ObservableCollection<ComposeService> Services { get; } = [];

    /// <summary>执行记录。</summary>
    public ObservableCollection<OutputLine> Output { get; } = [];

    /// <summary>当前项目。</summary>
    public ComposeProject? Selected
    {
        get => _selected;
        private set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(ProjectName), nameof(ProjectStatus),
                    nameof(ProjectPrefix), nameof(ProjectFile));
            }
        }
    }

    /// <summary>选了项目没有。</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>项目名。</summary>
    public string ProjectName => Selected?.Name ?? "";

    /// <summary>项目状态串。</summary>
    public string ProjectStatus => Selected?.Status ?? "";

    /// <summary>compose 命令的固定前缀(显示给用户看清楚)。</summary>
    public string ProjectPrefix => Selected is { } project
        ? $"-p {project.Name}  -f {project.PrimaryFile}  --project-directory {project.ProjectDirectory}"
        : "";

    /// <summary>compose 文件路径。</summary>
    public string ProjectFile => Selected?.PrimaryFile ?? "";

    /// <summary>compose 文件内容。</summary>
    public string Yaml
    {
        get => _yaml;
        set
        {
            if (SetField(ref _yaml, value))
            {
                OnPropertiesChanged(nameof(IsModified), nameof(ModifiedText));
            }
        }
    }

    /// <summary>改过了没有。</summary>
    public bool IsModified => _yaml != _originalYaml && _originalYaml.Length > 0;

    /// <summary>改动摘要。</summary>
    public string ModifiedText => IsModified ? "未保存 · 保存需手打 save" : "";

    /// <summary>出错信息。</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有没有出错。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>有 compose 命令在跑。</summary>
    public bool Running
    {
        get => _running;
        private set => SetField(ref _running, value);
    }

    /// <summary>远端有没有 compose v2。</summary>
    public bool ComposeAvailable
    {
        get => _composeAvailable;
        private set
        {
            if (SetField(ref _composeAvailable, value))
            {
                OnPropertyChanged(nameof(UnavailableHint));
            }
        }
    }

    /// <summary>compose 用不了时的说明。</summary>
    public string UnavailableHint =>
        "远端没有 docker compose(v2)。compose v1 那个独立的 docker-compose 没有 ls 子命令,面板列不出项目。";

    /// <summary>列表空了。</summary>
    public bool IsEmpty => LoadedOnce && Projects.Count == 0;

    /// <summary>选一个项目。</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>up -d。</summary>
    public RelayCommand UpCommand { get; }

    /// <summary>stop。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>restart。</summary>
    public RelayCommand RestartCommand { get; }

    /// <summary>pull。</summary>
    public RelayCommand PullCommand { get; }

    /// <summary>build。</summary>
    public RelayCommand BuildCommand { get; }

    /// <summary>down。</summary>
    public RelayCommand DownCommand { get; }

    /// <summary>down -v。</summary>
    public RelayCommand DownVolumesCommand { get; }

    /// <summary>展开配置(顺带校验语法)。</summary>
    public RelayCommand ConfigCommand { get; }

    /// <summary>保存 compose 文件。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>撤销未保存的修改。</summary>
    public RelayCommand RevertCommand { get; }

    /// <summary>清空执行记录。</summary>
    public RelayCommand ClearOutputCommand { get; }

    /// <summary>按路径打开一个项目。</summary>
    public RelayCommand OpenByPathCommand { get; }

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <inheritdoc />
    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Shell.Compose is not { } compose)
        {
            return;
        }
        Busy = true;
        try
        {
            ComposeAvailable = await compose.IsAvailableAsync(cancellationToken).ConfigureAwait(true);
            ComposeProject[] projects = ComposeAvailable
                ? await compose.ListProjectsAsync(cancellationToken).ConfigureAwait(true)
                : [];
            string? keep = Selected?.Name;
            Projects.Clear();
            foreach (ComposeProject project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Projects.Add(project);
            }
            LoadedOnce = true;
            OnPropertyChanged(nameof(IsEmpty));
            if (keep is not null && Projects.FirstOrDefault(p => p.Name == keep) is { } same)
            {
                Selected = same;
                await LoadServicesAsync(same, cancellationToken).ConfigureAwait(true);
            }
            else if (Selected is null && Projects.Count > 0)
            {
                await SelectAsync(Projects[0]).ConfigureAwait(true);
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
        Projects.Clear();
        Services.Clear();
        Output.Clear();
        Selected = null;
        Yaml = "";
        _originalYaml = "";
        Error = "";
        LoadedOnce = false;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type == "container";

    private async Task SelectAsync(ComposeProject project)
    {
        Selected = project;
        Error = "";
        await LoadServicesAsync(project, Shell.Lifetime).ConfigureAwait(true);
        await LoadYamlAsync(project).ConfigureAwait(true);
    }

    private async Task LoadServicesAsync(ComposeProject project, CancellationToken cancellationToken)
    {
        if (Shell.Compose is not { } compose)
        {
            return;
        }
        ComposeService[] services = await compose.ListServicesAsync(project, cancellationToken).ConfigureAwait(true);
        Services.Clear();
        foreach (ComposeService service in services)
        {
            Services.Add(service);
        }
    }

    private async Task LoadYamlAsync(ComposeProject project)
    {
        if (Shell.Compose is not { } compose || project.PrimaryFile.Length == 0)
        {
            Yaml = "";
            _originalYaml = "";
            return;
        }
        try
        {
            // 走 SFTP 直接读,不经 shell —— 免得被登录 shell 的输出、locale 与引用规则搅进来。
            _originalYaml = await compose.ReadFileAsync(project.PrimaryFile, Shell.Lifetime).ConfigureAwait(true);
            Yaml = _originalYaml;
            Error = "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _originalYaml = "";
            Yaml = "";
            Error = $"读不到 {project.PrimaryFile}:{ex.Message}";
        }
    }

    private void Log(string text, bool isError = false, bool isCommand = false)
    {
        Ui.Post(() =>
        {
            Output.Add(new(DateTimeOffset.Now.ToString("HH:mm:ss"), text, isError, isCommand));
            while (Output.Count > 2000)
            {
                Output.RemoveAt(0);
            }
        });
    }

    private async Task RunAsync(string arguments, string title)
    {
        if (Shell.Compose is not { } compose || Selected is not { } project || Running)
        {
            return;
        }
        Running = true;
        PanelTask task = Shell.Tasks.Start("Docker.boxes", $"{title} · {project.Name}", indeterminate: true);
        Log($"$ docker compose {ProjectPrefix} {arguments}", isCommand: true);
        try
        {
            int exit = await compose.RunAsync(project, arguments,
                new DirectProgress<ExecOutput>(output =>
                    Log(output.Line, output.Stream == ExecStream.StandardError)),
                task.Token).ConfigureAwait(true);
            if (exit == 0)
            {
                task.Finish(PanelTaskState.Succeeded, "完成");
                Log($"✔ {title} 完成 · 退出码 0");
                Shell.Feedback.Status(FeedbackKind.Success, $"{title} 完成 · {project.Name}");
            }
            else
            {
                task.Finish(PanelTaskState.Failed, $"退出码 {exit}");
                Log($"✘ {title} 失败 · 退出码 {exit}", isError: true);
                Shell.Feedback.Notify(FeedbackKind.Error, $"{title} 失败", $"{project.Name} · 退出码 {exit}",
                    new ToastAction("查看执行记录", () => { }));
            }
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            task.Finish(PanelTaskState.Cancelled, "已取消");
            Log("! 已取消", isError: true);
        }
        catch (Exception ex)
        {
            task.Finish(PanelTaskState.Failed, "失败", ex.Message);
            Log(ex.Message, isError: true);
            Shell.Feedback.ReportError(title, ex);
        }
        finally
        {
            Running = false;
        }
    }

    private async Task DownAsync(bool withVolumes)
    {
        if (Selected is not { } project)
        {
            return;
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = withVolumes ? $"down -v 项目 {project.Name}?" : $"down 项目 {project.Name}?",
            Icon = withVolumes ? "Docker.shield-alert" : "Icon.trash-2",
            Tier = withVolumes ? ConfirmTier.DataLoss : ConfirmTier.Destructive,
            ConfirmWord = "delete",
            ConfirmLabel = withVolumes ? "停止并删除卷" : "停止并删除容器",
            HostName = "",
            Commands = [$"docker compose {ProjectPrefix} down{(withVolumes ? " -v" : "")}"],
            CommandNote = "compose 会按依赖顺序停止并删除这个项目的容器与网络。",
            Consequences = withVolumes
                ? []
                :
                [
                    new(2, "项目的容器与默认网络会被删除。"),
                    new(1, "**命名卷不受影响** —— 数据还在,下次 up 会挂回去。"),
                    new(0, "compose 文件不动,随时可以再 up 起来。")
                ],
            DataLossHeadline = withVolumes ? "-v 会把这个项目的命名卷一起删掉,数据永久丢失" : null,
            DataLossPoints = withVolumes
                ?
                [
                    "数据库、上传目录、缓存 —— 只要是这个项目声明的命名卷,全部清空。",
                    "Docker 不做回收站,也没有快照。",
                    "只想停服务的话用不带 -v 的 down,或者直接 stop。"
                ]
                : []
        })).ConfigureAwait(true);
        if (confirmed)
        {
            await RunAsync(withVolumes ? "down -v" : "down", withVolumes ? "down -v" : "down").ConfigureAwait(true);
        }
    }

    private async Task ShowConfigAsync()
    {
        if (Shell.Compose is not { } compose || Selected is not { } project)
        {
            return;
        }
        Log($"$ docker compose {ProjectPrefix} config", isCommand: true);
        try
        {
            ExecResult result = await compose.ConfigAsync(project, Shell.Lifetime).ConfigureAwait(true);
            foreach (string line in (result.IsSuccess ? result.Output : result.Error).Split('\n'))
            {
                Log(line.TrimEnd('\r'), !result.IsSuccess);
            }
            Shell.Feedback.Status(result.IsSuccess ? FeedbackKind.Success : FeedbackKind.Error,
                result.IsSuccess ? "配置可以解析 —— 语法没问题" : $"配置有问题:{result.FailureText}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("展开配置", ex);
        }
    }

    private async Task SaveYamlAsync()
    {
        if (Shell.Compose is not { } compose || Selected is not { } project || !IsModified)
        {
            return;
        }
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"覆盖 {project.PrimaryFile}?",
            Icon = "Docker.shield-alert",
            Tier = ConfirmTier.DataLoss,
            ConfirmWord = "save",
            ConfirmLabel = "写回远端",
            ConfirmIcon = "Icon.save",
            HostName = "",
            Commands = [$"SFTP PUT {project.PrimaryFile}"],
            CommandNote = "经 SFTP 直接写,不经过 shell。",
            DataLossHeadline = "远端那份 compose 文件会被整体覆盖",
            DataLossPoints =
            [
                "面板不做备份,远端也没有版本历史 —— 除非那个目录在 git 里。",
                "保存**不会**自动 up:改动要等下一次 up -d 才生效。",
                "保存前建议先跑一次 config 确认语法能解析。"
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await compose.WriteFileAsync(project.PrimaryFile, Yaml, Shell.Lifetime).ConfigureAwait(true);
            _originalYaml = Yaml;
            OnPropertiesChanged(nameof(IsModified), nameof(ModifiedText));
            Shell.Feedback.Notify(FeedbackKind.Success, "已写回远端", project.PrimaryFile);
            Log($"✔ 已写回 {project.PrimaryFile}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("写回 compose 文件", ex);
        }
    }

    private async Task OpenByPathAsync()
    {
        var form = new OpenComposeForm();
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        var project = new ComposeProject(form.ProjectName, "(未起过)", form.FilePath);
        Projects.Add(project);
        await SelectAsync(project).ConfigureAwait(true);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>按路径打开一个 compose 项目。</summary>
public sealed class OpenComposeForm : PanelForm
{
    private readonly TextField _path;
    private readonly TextField _name;

    /// <summary>建表单。</summary>
    public OpenComposeForm()
    {
        _path = new("compose 文件路径") { Placeholder = "/srv/stacks/web-stack/compose.yaml" };
        _name = new("项目名") { Hint = "留空按目录名推导", Placeholder = "web-stack" };
        Watch(_path);
        Watch(_name);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "按路径打开项目";

    /// <inheritdoc />
    public override string Icon => "Icon.folder-open";

    /// <inheritdoc />
    public override string ConfirmLabel => "打开";

    /// <inheritdoc />
    public override string FooterHint => "compose ls 只认得起过至少一次的项目";

    /// <summary>文件路径。</summary>
    public string FilePath => _path.Value.Trim();

    /// <summary>项目名。</summary>
    public string ProjectName
    {
        get
        {
            if (_name.Value.Trim() is { Length: > 0 } explicitName)
            {
                return explicitName;
            }
            string directory = FilePath.TrimEnd('/');
            int lastSlash = directory.LastIndexOf('/');
            directory = lastSlash > 0 ? directory[..lastSlash] : directory;
            int nameSlash = directory.LastIndexOf('/');
            return nameSlash >= 0 ? directory[(nameSlash + 1)..] : directory;
        }
    }

    /// <inheritdoc />
    public override bool Validate()
    {
        if (FilePath.Length == 0)
        {
            _path.Error = "路径不能为空。";
            return false;
        }
        if (!FilePath.StartsWith('/'))
        {
            _path.Error = "要用绝对路径 —— 相对路径会以登录目录为基准,那多半不是你想要的。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = $"docker compose -p {ProjectName} -f {FilePath} --project-directory <文件所在目录>";
        CommandNote = "三个参数一起钉住:少 -f 找不到 yml,少 -p 项目名会被重新推导,少 --project-directory 里面的相对路径会挂错盘。";
    }
}
