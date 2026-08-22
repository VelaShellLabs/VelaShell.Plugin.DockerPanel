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

/// <summary>Compose 页的页签。</summary>
public enum ComposeTab
{
    /// <summary>compose.yaml。</summary>
    Yaml,

    /// <summary>.env。</summary>
    Env,

    /// <summary>服务列表。</summary>
    Services,

    /// <summary>展开后的配置。</summary>
    Config,

    /// <summary>项目的合并日志。</summary>
    Logs
}

/// <summary>Compose 页。</summary>
public sealed class ComposePageViewModel : PageViewModel
{
    private ComposeProject? _selected;
    private string _yaml = "";
    private string _originalYaml = "";
    private string _env = "";
    private string _originalEnv = "";
    private string _config = "";
    private string _error = "";
    private bool _running;
    private bool _composeAvailable = true;
    private ComposeTab _tab = ComposeTab.Yaml;
    private CancellationTokenSource? _logsCts;
    private int _lastExitCode;

    /// <summary>当前页签。</summary>
    public ComposeTab Tab
    {
        get => _tab;
        private set
        {
            if (SetField(ref _tab, value))
            {
                OnPropertiesChanged(nameof(IsYamlTab), nameof(IsEnvTab), nameof(IsServicesTab),
                    nameof(IsConfigTab), nameof(IsLogsTab));
            }
        }
    }

    /// <summary>在 compose.yaml 页签。</summary>
    public bool IsYamlTab => Tab == ComposeTab.Yaml;

    /// <summary>在 .env 页签。</summary>
    public bool IsEnvTab => Tab == ComposeTab.Env;

    /// <summary>在服务页签。</summary>
    public bool IsServicesTab => Tab == ComposeTab.Services;

    /// <summary>在 config 展开页签。</summary>
    public bool IsConfigTab => Tab == ComposeTab.Config;

    /// <summary>在合并日志页签。</summary>
    public bool IsLogsTab => Tab == ComposeTab.Logs;

    /// <summary>项目的 <c>.env</c> 内容。</summary>
    public string Env
    {
        get => _env;
        set
        {
            if (SetField(ref _env, value))
            {
                OnPropertiesChanged(nameof(EnvModified), nameof(EnvModifiedText));
            }
        }
    }

    /// <summary><c>.env</c> 改过了。</summary>
    public bool EnvModified => _env != _originalEnv;

    /// <summary><c>.env</c> 的未保存提示。</summary>
    public string EnvModifiedText => EnvModified ? "未保存 · 保存需手打 save" : "";

    /// <summary>这个项目有没有 <c>.env</c>。</summary>
    public bool HasEnv
    {
        get => _hasEnv;
        private set => SetField(ref _hasEnv, value);
    }

    private bool _hasEnv;

    /// <summary><c>compose config</c> 展开后的结果。</summary>
    public string Config
    {
        get => _config;
        private set => SetField(ref _config, value);
    }

    /// <summary>合并日志的行。</summary>
    public ObservableCollection<OutputLine> Logs { get; } = [];

    /// <summary>日志正在跟随。</summary>
    public bool LogsFollowing
    {
        get => _logsFollowing;
        private set => SetField(ref _logsFollowing, value);
    }

    private bool _logsFollowing;

    /// <summary>最近一次子命令的退出码文本(执行记录头上那个 chip)。</summary>
    public string ExitCodeText => _lastExitCode == 0 ? "退出码 0" : $"退出码 {_lastExitCode}";

    /// <summary>最近一次子命令的语气。</summary>
    public RowTone ExitTone => _lastExitCode == 0 ? RowTone.Ok : RowTone.Danger;

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
        SetTabCommand = new RelayCommand(p => p is ComposeTab tab ? SetTabAsync(tab) : Task.CompletedTask);
        SaveEnvCommand = new RelayCommand(_ => SaveEnvAsync());
        RevertEnvCommand = new RelayCommand(_ =>
        {
            Env = _originalEnv;
            return Task.CompletedTask;
        });
        ServiceLogsCommand = new RelayCommand(p => p is ComposeService s
            ? RunForServiceAsync("logs --tail 200 --no-color", s, $"{s.Service} 的日志")
            : Task.CompletedTask);
        ServiceRestartCommand = new RelayCommand(p => p is ComposeService s
            ? RunForServiceAsync("restart", s, $"重启 {s.Service}")
            : Task.CompletedTask);
        ServiceTerminalCommand = new RelayCommand(p => p is ComposeService s
            ? OpenServiceTerminalAsync(s)
            : Task.CompletedTask);
        ToggleLogsCommand = new RelayCommand(_ => ToggleLogsAsync());
        ClearLogsCommand = new RelayCommand(_ =>
        {
            Logs.Clear();
            return Task.CompletedTask;
        });
        CopyOutputCommand = new RelayCommand(_ => Shell.Context.Clipboard.SetTextAsync(
            string.Join('\n', Output.Select(o => $"{o.Time} {o.Text}")), Shell.Lifetime));
        NewProjectCommand = new RelayCommand(_ => NewProjectAsync());
    }

    /// <summary>切页签。</summary>
    public RelayCommand SetTabCommand { get; }

    /// <summary>保存 <c>.env</c>。</summary>
    public RelayCommand SaveEnvCommand { get; }

    /// <summary>撤销 <c>.env</c> 的改动。</summary>
    public RelayCommand RevertEnvCommand { get; }

    /// <summary>看某个服务的日志。</summary>
    public RelayCommand ServiceLogsCommand { get; }

    /// <summary>重启某个服务。</summary>
    public RelayCommand ServiceRestartCommand { get; }

    /// <summary>进某个服务的终端。</summary>
    public RelayCommand ServiceTerminalCommand { get; }

    /// <summary>开始 / 停止跟随合并日志。</summary>
    public RelayCommand ToggleLogsCommand { get; }

    /// <summary>清空合并日志。</summary>
    public RelayCommand ClearLogsCommand { get; }

    /// <summary>复制执行记录。</summary>
    public RelayCommand CopyOutputCommand { get; }

    /// <summary>新建一个 compose 项目。</summary>
    public RelayCommand NewProjectCommand { get; }

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

    /// <summary>
    /// 把用户送到 Compose 页并选中这个项目 —— 执行记录就在那一页的右侧那一栏。
    /// <para>已经在这一页、而且选的就是它,就什么都不做:重选一次会把刚出的那份记录冲掉。</para>
    /// </summary>
    private async Task ShowOutputAsync(ComposeProject project)
    {
        await Shell.GoToAsync(PanelPage.Compose).ConfigureAwait(true);
        if (Selected?.Name != project.Name)
        {
            await SelectAsync(project).ConfigureAwait(true);
        }
    }

    private async Task SelectAsync(ComposeProject project)
    {
        await StopLogsAsync().ConfigureAwait(true);
        Selected = project;
        Error = "";
        Config = "";
        Logs.Clear();
        Tab = ComposeTab.Yaml;
        await LoadServicesAsync(project, Shell.Lifetime).ConfigureAwait(true);
        await LoadYamlAsync(project).ConfigureAwait(true);
        await LoadEnvAsync(project).ConfigureAwait(true);
    }

    private async Task SetTabAsync(ComposeTab tab)
    {
        Tab = tab;
        switch (tab)
        {
            // config 展开要跑一次远端命令,只在进这一页时跑,而且只跑一次。
            case ComposeTab.Config when Config.Length == 0:
                await ShowConfigAsync().ConfigureAwait(true);
                break;
            case ComposeTab.Logs when !LogsFollowing && Logs.Count == 0:
                await ToggleLogsAsync().ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// 读项目的 <c>.env</c>。
    /// <para>
    /// 读不到就当没有 —— 大多数项目根本没有这个文件,把"文件不存在"报成错误
    /// 会让一个正常的项目看起来出了问题。
    /// </para>
    /// </summary>
    private async Task LoadEnvAsync(ComposeProject project)
    {
        _originalEnv = "";
        Env = "";
        HasEnv = false;
        if (Shell.Compose is not { } compose || ComposeCli.EnvPath(project) is not { Length: > 0 } path)
        {
            return;
        }
        try
        {
            _originalEnv = await compose.ReadFileAsync(path, Shell.Lifetime).ConfigureAwait(true);
            Env = _originalEnv;
            HasEnv = true;
        }
        catch (Exception)
        {
            HasEnv = false;
        }
    }

    private async Task SaveEnvAsync()
    {
        if (Shell.Compose is not { } compose || Selected is not { } project || !EnvModified)
        {
            return;
        }
        string path = ComposeCli.EnvPath(project);
        bool confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"覆盖 {path}?",
            Icon = "Docker.shield-alert",
            Tier = ConfirmTier.DataLoss,
            ConfirmWord = "save",
            ConfirmLabel = "写回远端",
            ConfirmIcon = "Icon.save",
            HostName = "",
            Commands = [$"SFTP PUT {path}"],
            CommandNote = ".env 经 SFTP 覆盖写,不经 shell。",
            DataLossHeadline = "原文件会被整体覆盖,无法撤销",
            DataLossPoints =
            [
                "这里常放的是口令与密钥 —— 覆盖之前确认你手上这份是完整的。",
                "已经在跑的容器不会因此重新读取环境变量,要 up -d 重建才生效。"
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await compose.WriteFileAsync(path, Env, Shell.Lifetime).ConfigureAwait(true);
            _originalEnv = Env;
            OnPropertiesChanged(nameof(EnvModified), nameof(EnvModifiedText));
            HasEnv = true;
            Log($"✔ 已写回 {path}");
            Shell.Feedback.Notify(FeedbackKind.Success, "已写回 .env",
                "已在跑的容器要 up -d 重建才会读到新值。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("写回 .env", ex);
        }
    }

    private async Task RunForServiceAsync(string arguments, ComposeService service, string title)
    {
        if (Shell.Compose is not { } compose || Selected is not { } project || Running)
        {
            return;
        }
        Running = true;
        Log($"$ docker compose {ProjectPrefix} {arguments} {service.Service}", isCommand: true);
        try
        {
            int exit = await compose.RunForServiceAsync(project, arguments, service.Service,
                new DirectProgress<ExecOutput>(output =>
                    Log(output.Line, output.Stream == ExecStream.StandardError)),
                Shell.Lifetime).ConfigureAwait(true);
            _lastExitCode = exit;
            OnPropertiesChanged(nameof(ExitCodeText), nameof(ExitTone));
            Log(exit == 0 ? $"✔ {title} 完成" : $"✘ {title} 失败 · 退出码 {exit}", isError: exit != 0);
            await LoadServicesAsync(project, Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(ex.Message, isError: true);
            Shell.Feedback.ReportError(title, ex);
        }
        finally
        {
            Running = false;
        }
    }

    /// <summary>进这个服务对应容器的终端(借容器页的详情抽屉)。</summary>
    private async Task OpenServiceTerminalAsync(ComposeService service)
    {
        await Shell.GoToAsync(PanelPage.Containers).ConfigureAwait(true);
        ContainerRow? row = Shell.Containers.View.FirstOrDefault(r => r.Name == service.Name);
        if (row is null)
        {
            Shell.Feedback.Status(FeedbackKind.Warning, $"容器列表里没有 {service.Name} —— 它可能还没起来。");
            return;
        }
        Shell.Containers.RowTerminalCommand.Execute(row);
    }

    /// <summary>
    /// 跟随项目的合并日志。
    /// <para>
    /// 交给 <c>compose logs -f</c> 去并,而不是面板自己并 N 条 <c>docker logs</c> ——
    /// compose 认得项目里有哪些服务,包括面板列表还没刷到的那些。
    /// </para>
    /// </summary>
    private async Task ToggleLogsAsync()
    {
        if (LogsFollowing)
        {
            await StopLogsAsync().ConfigureAwait(true);
            return;
        }
        if (Shell.Compose is not { } compose || Selected is not { } project)
        {
            return;
        }
        _logsCts = CancellationTokenSource.CreateLinkedTokenSource(Shell.Lifetime);
        CancellationToken token = _logsCts.Token;
        LogsFollowing = true;
        string tail = Shell.Settings.LogTail;
        _ = Task.Run(async () =>
        {
            try
            {
                await compose.FollowLogsAsync(project, tail == "all" ? "all" : tail,
                    new DirectProgress<ExecOutput>(output => Ui.Post(() =>
                    {
                        Logs.Add(new(DateTimeOffset.Now.ToString("HH:mm:ss"), output.Line,
                            output.Stream == ExecStream.StandardError, false));
                        while (Logs.Count > 5000)
                        {
                            Logs.RemoveAt(0);
                        }
                    })), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Ui.Post(() => Logs.Add(new(DateTimeOffset.Now.ToString("HH:mm:ss"),
                    $"日志流断开:{ex.Message}", true, false)));
            }
            Ui.Post(() => LogsFollowing = false);
        }, token);
    }

    private async Task StopLogsAsync()
    {
        if (_logsCts is not { } cts)
        {
            return;
        }
        await cts.CancelAsync().ConfigureAwait(true);
        cts.Dispose();
        _logsCts = null;
        LogsFollowing = false;
    }

    /// <summary>
    /// 新建一个 compose 项目:写一个骨架 <c>compose.yaml</c>,然后按路径打开它。
    /// <para>
    /// <b>不</b>顺手 <c>up -d</c> —— 骨架里的服务是占位的,起起来只会得到一个失败的容器。
    /// </para>
    /// </summary>
    private async Task NewProjectAsync()
    {
        if (Shell.Compose is not { } compose)
        {
            return;
        }
        var form = new NewComposeProjectForm();
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        string path = $"{form.Directory.TrimEnd('/')}/compose.yaml";
        try
        {
            await compose.WriteFileAsync(path, NewComposeProjectForm.Skeleton(form.ProjectName), Shell.Lifetime)
                         .ConfigureAwait(true);
            Log($"✔ 已创建 {path}");
            Shell.Feedback.Notify(FeedbackKind.Success, "项目已创建",
                $"{path} —— 改完 compose.yaml 之后按 up -d 起它。");
            await OpenPathAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("新建 Compose 项目", ex);
        }
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
            _lastExitCode = exit;
            OnPropertiesChanged(nameof(ExitCodeText), nameof(ExitTone));
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
                // 执行记录就在 Compose 页右侧那一栏 —— 把用户送过去并选中出事的那个项目。
                // 原来这颗按钮挂的是一个空 lambda:点了什么都不会发生。
                Shell.Feedback.Notify(FeedbackKind.Error, $"{title} 失败", $"{project.Name} · 退出码 {exit}",
                    new ToastAction("查看执行记录", () => _ = ShowOutputAsync(project)));
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
                    new(1, "命名卷不受影响 —— 数据还在,下次 up 会挂回去。"),
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
        Config = "正在展开…";
        try
        {
            ExecResult result = await compose.ConfigAsync(project, Shell.Lifetime).ConfigureAwait(true);
            // 展开的结果进它自己那一页,不再倒进执行记录 —— 几百行 YAML 会把记录冲干净。
            Config = result.IsSuccess ? result.Output : result.Error;
            Log(result.IsSuccess ? "✔ 配置可以解析 —— 语法没问题" : "✘ 配置有问题(见 config 页签)",
                isError: !result.IsSuccess);
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
                "保存不会自动 up:改动要等下一次 up -d 才生效。",
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
        await OpenPathAsync(form.FilePath, form.ProjectName).ConfigureAwait(true);
    }

    /// <summary>把一个 compose 文件加进项目列表并选中它。</summary>
    private async Task OpenPathAsync(string filePath, string? projectName = null)
    {
        // compose 的项目名默认取项目目录名 —— 与 compose 自己的规则一致,
        // 不一致的话面板起的容器会带上一个和命令行不同的前缀。
        string directory = filePath[..Math.Max(0, filePath.LastIndexOf('/'))];
        string name = projectName is { Length: > 0 } given
            ? given
            : directory[(directory.LastIndexOf('/') + 1)..];
        var project = new ComposeProject(name, "(未起过)", filePath);
        if (Projects.All(p => p.PrimaryFile != filePath))
        {
            Projects.Add(project);
        }
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

/// <summary>
/// 新建一个 compose 项目。
/// <para>
/// 只写一个骨架 <c>compose.yaml</c>,**不**顺手 <c>up -d</c> ——
/// 骨架里的服务是占位的,起起来只会得到一个失败的容器和一条看不懂的报错。
/// </para>
/// </summary>
public sealed class NewComposeProjectForm : PanelForm
{
    private readonly TextField _directory;
    private readonly TextField _name;

    /// <summary>建表单。</summary>
    public NewComposeProjectForm()
    {
        _directory = new("项目目录") { Placeholder = "/srv/stacks/new-stack" };
        _name = new("项目名") { Hint = "留空按目录名推导", Placeholder = "new-stack" };
        Watch(_directory);
        Watch(_name);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "新建 Compose 项目";

    /// <inheritdoc />
    public override string Icon => "Docker.file-code";

    /// <inheritdoc />
    public override string ConfirmLabel => "创建";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.plus";

    /// <inheritdoc />
    public override string FooterHint => "只写文件,不起容器 —— 改完 compose.yaml 再按 up -d";

    /// <summary>项目目录。</summary>
    public string Directory => _directory.Value.Trim();

    /// <summary>项目名。</summary>
    public string ProjectName
    {
        get
        {
            if (_name.Value.Trim() is { Length: > 0 } explicitName)
            {
                return explicitName;
            }
            string directory = Directory.TrimEnd('/');
            int slash = directory.LastIndexOf('/');
            return slash >= 0 ? directory[(slash + 1)..] : directory;
        }
    }

    /// <summary>骨架内容。注释里写清下一步该干什么,而不是丢一个空文件给用户。</summary>
    public static string Skeleton(string projectName) =>
        $"""
         # {projectName} —— 由 VelaShell Docker 面板创建
         # 把下面这个占位服务换成你自己的,然后在面板里按 up -d。

         services:
           app:
             image: nginx:alpine
             restart: unless-stopped
             ports:
               - "8080:80"

         """;

    /// <inheritdoc />
    public override bool Validate()
    {
        if (Directory.Length == 0)
        {
            _directory.Error = "目录不能为空。";
            return false;
        }
        if (!Directory.StartsWith('/'))
        {
            _directory.Error = "要用绝对路径 —— 相对路径会以登录目录为基准,那多半不是你想要的。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = $"SFTP PUT {Directory.TrimEnd('/')}/compose.yaml";
        CommandNote = $"随后用 -p {ProjectName} 打开它。目录必须已经存在 —— 面板不替你 mkdir。";
    }
}
