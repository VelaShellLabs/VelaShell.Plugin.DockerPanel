using Avalonia.Controls;
using Avalonia.Media;
using System.Text;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    /// <summary>执行记录保留的条数。够看清"刚才那几步做了什么",又不至于把内存吃成一个日志缓冲区。</summary>
    private const int CommandLogCapacity = 300;

    private readonly List<CommandLogEntry> _commandLog = [];

    private DrawerTab _drawerContent = DrawerTab.Details;
    private bool _isDrawerOpen = true;
    private GridLength _drawerHeight = new(260);
    private GridLength _savedDrawerHeight = new(260);
    private string _drawerText = string.Empty;
    private string _drawerTitle = string.Empty;
    private bool _drawerWrap;
    private string _logRaw = string.Empty;
    private string _logFilter = string.Empty;
    private int _logTail = 500;
    private bool _logTimestamps = true;
    private bool _logFollow;

    /// <summary>抽屉当前显示的是什么。</summary>
    public DrawerTab DrawerContent
    {
        get => _drawerContent;
        private set
        {
            if (!SetProperty(ref _drawerContent, value))
            {
                return;
            }
            RaisePropertyChanged(nameof(IsDetailsDrawer));
            RaisePropertyChanged(nameof(IsLogsDrawer));
            RaisePropertyChanged(nameof(IsTopDrawer));
            RaisePropertyChanged(nameof(IsDiffDrawer));
            RaisePropertyChanged(nameof(IsPortsDrawer));
            RaisePropertyChanged(nameof(IsHistoryDrawer));
            RaisePropertyChanged(nameof(IsServicesDrawer));
            RaisePropertyChanged(nameof(IsConfigDrawer));
            RaisePropertyChanged(nameof(IsFileDrawer));
            RaisePropertyChanged(nameof(IsOutputDrawer));
            RaisePropertyChanged(nameof(IsDrawerEditable));
            SaveDrawerCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>抽屉是否展开。</summary>
    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set
        {
            if (!SetProperty(ref _isDrawerOpen, value))
            {
                return;
            }
            // 收起时把当前高度记下来再压到 0:再次展开时回到用户拖出来的那个高度,
            // 而不是每次都弹回默认值。
            if (value)
            {
                DrawerHeight = _savedDrawerHeight;
            }
            else
            {
                if (DrawerHeight.IsAbsolute && DrawerHeight.Value > 40)
                {
                    _savedDrawerHeight = DrawerHeight;
                }
                DrawerHeight = new(0);
            }
        }
    }

    /// <summary>
    /// 抽屉那一行的高度。直接**双向**绑到 <c>RowDefinition.Height</c> ——
    /// 用户拖分割条改的就是它,收起时把它压成 0 就等于折叠(而不是留一个看不见的空行)。
    /// </summary>
    public GridLength DrawerHeight
    {
        get => _drawerHeight;
        set => SetProperty(ref _drawerHeight, value);
    }

    /// <summary>抽屉正文。仅 compose 文件编辑时可写。</summary>
    public string DrawerText
    {
        get => _drawerText;
        set => SetProperty(ref _drawerText, value);
    }

    /// <summary>抽屉标题(当前对着谁)。</summary>
    public string DrawerTitle
    {
        get => _drawerTitle;
        private set => SetProperty(ref _drawerTitle, value);
    }

    /// <summary>正文是否自动换行。inspect 的 JSON 与日志都很宽,默认不换、留横向滚动条。</summary>
    public bool DrawerWrap
    {
        get => _drawerWrap;
        set
        {
            if (SetProperty(ref _drawerWrap, value))
            {
                RaisePropertyChanged(nameof(DrawerTextWrapping));
            }
        }
    }

    /// <summary>换行开关的控件形态(直接给 <c>TextBox.TextWrapping</c> 绑,省一个转换器)。</summary>
    public TextWrapping DrawerTextWrapping => DrawerWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    /// <summary>正在看 inspect。</summary>
    public bool IsDetailsDrawer => DrawerContent is DrawerTab.Details;

    /// <summary>正在看日志。</summary>
    public bool IsLogsDrawer => DrawerContent is DrawerTab.Logs;

    /// <summary>正在看进程表。</summary>
    public bool IsTopDrawer => DrawerContent is DrawerTab.Top;

    /// <summary>正在看文件变更。</summary>
    public bool IsDiffDrawer => DrawerContent is DrawerTab.Diff;

    /// <summary>正在看端口。</summary>
    public bool IsPortsDrawer => DrawerContent is DrawerTab.Ports;

    /// <summary>正在看构建历史。</summary>
    public bool IsHistoryDrawer => DrawerContent is DrawerTab.History;

    /// <summary>正在看 compose 服务列表。</summary>
    public bool IsServicesDrawer => DrawerContent is DrawerTab.Services;

    /// <summary>正在看 compose 展开配置。</summary>
    public bool IsConfigDrawer => DrawerContent is DrawerTab.Config;

    /// <summary>正在编辑 compose 文件。</summary>
    public bool IsFileDrawer => DrawerContent is DrawerTab.File;

    /// <summary>正在看执行记录。</summary>
    public bool IsOutputDrawer => DrawerContent is DrawerTab.Output;

    /// <summary>正文可编辑(只有 compose 文件这一档)。</summary>
    public bool IsDrawerEditable => DrawerContent is DrawerTab.File;

    /// <summary>日志取多少行。</summary>
    public int LogTail
    {
        get => _logTail;
        set
        {
            if (SetProperty(ref _logTail, value))
            {
                _ = SaveSettingAsync("logTail", value);
            }
        }
    }

    /// <summary>日志下拉的选中项。</summary>
    public FormChoice? SelectedLogTailChoice
    {
        get => LogTailChoices.FirstOrDefault(c => c.Value == LogTail.ToString(System.Globalization.CultureInfo.InvariantCulture))
               ?? LogTailChoices[1];
        set
        {
            if (value is not null && int.TryParse(value.Value, out var tail))
            {
                LogTail = tail;
                RaisePropertyChanged();
                _ = LoadDrawerAsync(true);
            }
        }
    }

    /// <summary>日志行数的可选项。</summary>
    public IReadOnlyList<FormChoice> LogTailChoices { get; } =
    [
        new("100", "100"),
        new("500", "500"),
        new("2000", "2000"),
        new("10000", "10000")
    ];

    /// <summary>日志是否带时间戳。</summary>
    public bool LogTimestamps
    {
        get => _logTimestamps;
        set
        {
            if (SetProperty(ref _logTimestamps, value))
            {
                _ = LoadDrawerAsync(true);
            }
        }
    }

    /// <summary>
    /// 日志是否"跟随"。
    /// <para>
    /// 就是 <c>docker logs -f</c> —— 一条真正的流,新日志到达即出现,不是按间隔补拉。
    /// (SDK 1.1 之前远程执行只有一次性形态,<c>-f</c> 永远不返回,那时只能用
    /// <c>--since &lt;上一条时间戳&gt;</c> 反复补拉,还要自己处理闭区间重复。)
    /// </para>
    /// </summary>
    public bool LogFollow
    {
        get => _logFollow;
        set
        {
            if (!SetProperty(ref _logFollow, value))
            {
                return;
            }
            if (value && DrawerContent is DrawerTab.Logs)
            {
                StartLogStream();
            }
            else
            {
                StopLogStream();
            }
        }
    }

    /// <summary>日志的本地过滤串(只影响显示,不改远端取的量)。</summary>
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (SetProperty(ref _logFilter, value))
            {
                PublishLog();
            }
        }
    }

    /// <summary>当前页签下,抽屉里能显示"详情"。</summary>
    public bool CanShowDetails => HasDrawerTarget;

    /// <summary>能显示日志(容器页,且选中了一个容器)。</summary>
    public bool CanShowLogs => IsContainersTab && PrimaryContainer is not null;

    /// <summary>能显示进程 / 文件变更 / 端口(同上)。</summary>
    public bool CanShowContainerExtras => CanShowLogs;

    /// <summary>能显示构建历史(镜像页)。</summary>
    public bool CanShowHistory => IsImagesTab && PrimaryImage is not null;

    /// <summary>能显示 compose 的服务 / 配置 / 文件。</summary>
    public bool CanShowComposeExtras => IsComposeTab && SelectedCompose is not null;

    private bool HasDrawerTarget => ActiveTab switch
    {
        DockerTab.Containers => PrimaryContainer is not null,
        DockerTab.Images => PrimaryImage is not null,
        DockerTab.Volumes => PrimaryVolume is not null,
        DockerTab.Networks => PrimaryNetwork is not null,
        DockerTab.Compose => SelectedCompose is not null,
        _ => false
    };

    /// <summary>引擎每执行一条命令就记一笔(在任意线程被调用)。</summary>
    /// <param name="command">远端命令。</param>
    /// <param name="result">结果。</param>
    /// <param name="elapsed">耗时。</param>
    private void OnCommandObserved(string command, ExecResult result, TimeSpan elapsed)
    {
        lock (_commandLog)
        {
            _commandLog.Add(new(DateTimeOffset.Now, command, result.ExitCode, elapsed));
            if (_commandLog.Count > CommandLogCapacity)
            {
                _commandLog.RemoveRange(0, _commandLog.Count - CommandLogCapacity);
            }
        }
        if (DrawerContent is DrawerTab.Output && IsDrawerOpen)
        {
            DrawerText = RenderCommandLog();
        }
    }

    private string RenderCommandLog()
    {
        StringBuilder builder = new();
        lock (_commandLog)
        {
            foreach (var entry in _commandLog)
            {
                builder.AppendLine(entry.Line);
            }
        }
        return builder.ToString();
    }

    private async Task SelectDrawerAsync(string tab)
    {
        if (!Enum.TryParse(tab, out DrawerTab parsed))
        {
            return;
        }
        await ShowDrawerAsync(parsed).ConfigureAwait(true);
    }

    private async Task ShowDrawerAsync(DrawerTab tab)
    {
        DrawerContent = tab;
        IsDrawerOpen = true;
        await LoadDrawerAsync(true).ConfigureAwait(true);
    }

    /// <summary>直接把一段现成的文本摆进抽屉(拉取 / prune / compose 的输出)。</summary>
    /// <param name="tab">抽屉档位。</param>
    /// <param name="text">文本。</param>
    private void ShowDrawerText(DrawerTab tab, string text)
    {
        if (tab is DrawerTab.Output)
        {
            // 执行记录本来就在积累;这里把这一条命令的**完整输出**追加上去,
            // 而不是把记录整个换成它 —— 用户往上翻还能看到前几步。
            lock (_commandLog)
            {
                _commandLog.Add(new(DateTimeOffset.Now, text.Split('\n')[0], 0, TimeSpan.Zero));
            }
            DrawerContent = DrawerTab.Output;
            IsDrawerOpen = true;
            DrawerText = RenderCommandLog() + "\n" + text;
            DrawerTitle = _loc["Drawer_Output"];
            return;
        }
        DrawerContent = tab;
        IsDrawerOpen = true;
        DrawerText = text;
    }

    private void ResetDrawerForTab()
    {
        RaisePropertyChanged(nameof(CanShowDetails));
        RaisePropertyChanged(nameof(CanShowLogs));
        RaisePropertyChanged(nameof(CanShowContainerExtras));
        RaisePropertyChanged(nameof(CanShowHistory));
        RaisePropertyChanged(nameof(CanShowComposeExtras));
        DrawerContent = DrawerContent switch
        {
            DrawerTab.Output => DrawerTab.Output,
            _ when ActiveTab is DockerTab.Containers => DrawerTab.Details,
            _ when ActiveTab is DockerTab.Images => DrawerTab.Details,
            _ when ActiveTab is DockerTab.Compose => DrawerTab.Services,
            _ => DrawerTab.Details
        };
    }

    /// <summary>按当前选中项与抽屉档位,把内容取回来。</summary>
    /// <param name="force">强制重取(用户主动切档 / 按刷新);为 false 时只在抽屉开着才取。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task LoadDrawerAsync(bool force)
    {
        RaisePropertyChanged(nameof(CanShowDetails));
        RaisePropertyChanged(nameof(CanShowLogs));
        RaisePropertyChanged(nameof(CanShowContainerExtras));
        RaisePropertyChanged(nameof(CanShowHistory));
        RaisePropertyChanged(nameof(CanShowComposeExtras));
        ComposeEditCommand.RaiseCanExecuteChanged();
        if (_api is not { } api || (!IsDrawerOpen && !force))
        {
            return;
        }
        if (DrawerContent is DrawerTab.Output)
        {
            DrawerTitle = _loc["Drawer_Output"];
            DrawerText = RenderCommandLog();
            return;
        }
        // 选中项换了就得重新拉:上一条的日志留在屏幕上、标题却已经是新容器,是最糟的一种"看起来对"。
        StopLogStream();
        switch (ActiveTab)
        {
            case DockerTab.Containers when PrimaryContainer is { } container:
                DrawerTitle = container.Model.Name;
                if (DrawerContent is DrawerTab.Logs && LogFollow)
                {
                    // 跟随是一条真流:`--tail` 先补历史,之后新行自己来。这里就不再取快照了,
                    // 否则屏幕上会先出现一份历史、再被流里的同一份历史盖一遍。
                    StartLogStream();
                    break;
                }
                DrawerText = DrawerContent switch
                {
                    DrawerTab.Logs => await LoadLogsAsync(api, container.Model.Id).ConfigureAwait(true),
                    DrawerTab.Top => await GuardAsync(token => api.TopAsync(container.Model.Id, token)).ConfigureAwait(true),
                    DrawerTab.Diff => await GuardAsync(token => api.DiffAsync(container.Model.Id, token)).ConfigureAwait(true),
                    DrawerTab.Ports => await GuardAsync(token => api.PortsAsync(container.Model.Id, token)).ConfigureAwait(true),
                    _ => await GuardAsync(token => api.InspectContainerAsync(container.Model.Id, token)).ConfigureAwait(true)
                };
                break;
            case DockerTab.Images when PrimaryImage is { } image:
                DrawerTitle = image.Model.Display;
                DrawerText = DrawerContent switch
                {
                    DrawerTab.History => await GuardAsync(token => api.ImageHistoryAsync(image.Model.Reference, token)).ConfigureAwait(true),
                    _ => await GuardAsync(token => api.InspectImageAsync(image.Model.Reference, token)).ConfigureAwait(true)
                };
                break;
            case DockerTab.Volumes when PrimaryVolume is { } volume:
                DrawerTitle = volume.Model.Name;
                DrawerText = await GuardAsync(token => api.InspectVolumeAsync(volume.Model.Name, token)).ConfigureAwait(true);
                break;
            case DockerTab.Networks when PrimaryNetwork is { } network:
                DrawerTitle = network.Model.Name;
                DrawerText = await GuardAsync(token => api.InspectNetworkAsync(network.Model.Name, token)).ConfigureAwait(true);
                break;
            case DockerTab.Compose when SelectedCompose is { } project:
                DrawerTitle = project.Model.Name;
                DrawerText = DrawerContent switch
                {
                    DrawerTab.Config => await GuardAsync(token => api.ComposeConfigAsync(ComposeProjectName, ComposeConfigFile, token))
                        .ConfigureAwait(true),
                    DrawerTab.Logs => await GuardAsync(token => api.ComposeLogsAsync(ComposeProjectName, ComposeConfigFile, LogTail, token))
                        .ConfigureAwait(true),
                    DrawerTab.File => await ReadComposeFileAsync().ConfigureAwait(true),
                    _ => await GuardAsync(token => api.ComposePsAsync(ComposeProjectName, ComposeConfigFile, token)).ConfigureAwait(true)
                };
                break;
            default:
                DrawerTitle = string.Empty;
                DrawerText = string.Empty;
                break;
        }
    }

    /// <summary>
    /// 取一次日志快照(不跟随时用)。跟随打开时走的是 <see cref="StartLogStream" /> 的真流,
    /// 这里不参与。
    /// </summary>
    /// <param name="api">API。</param>
    /// <param name="containerId">容器 id。</param>
    /// <returns>过滤后的日志文本。</returns>
    private async Task<string> LoadLogsAsync(DockerApi api, string containerId)
    {
        var result = await GuardAsync(
            token => api.LogsAsync(containerId, LogTail, LogTimestamps, string.Empty, token)).ConfigureAwait(true);
        _logRaw = result.Output;
        return FilterLog(_logRaw);
    }

    private void PublishLog()
    {
        if (DrawerContent is DrawerTab.Logs)
        {
            DrawerText = FilterLog(_logRaw);
        }
    }

    private string FilterLog(string text)
    {
        var needle = LogFilter.Trim();
        if (needle.Length == 0 || text.Length == 0)
        {
            return text;
        }
        StringBuilder builder = new();
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(line);
            }
        }
        return builder.ToString().TrimEnd('\n');
    }

    private async Task SaveDrawerAsync()
    {
        if (DrawerContent is DrawerTab.File)
        {
            await SaveComposeFileAsync().ConfigureAwait(true);
        }
    }
}
