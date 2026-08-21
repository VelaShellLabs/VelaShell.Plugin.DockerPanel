using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Platform.Storage;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>编辑器 / 差异视图里的一行。</summary>
/// <param name="Number">显示的行号。</param>
/// <param name="Marker">差异标记(<c>+</c> / <c>-</c> / <c>~</c>;没变为空)。</param>
/// <param name="Text">行内容。</param>
/// <param name="Tone">语气(界面按它给标记与底色上色)。</param>
public readonly record struct EditorLine(string Number, string Marker, string Text, RowTone Tone);

/// <summary>本面板往这个容器里写过的一次。</summary>
/// <param name="At">什么时候。</param>
/// <param name="Path">写了哪个文件。</param>
/// <param name="Summary">改动摘要,如 <c>+2 −1</c> 或 <c>新建</c>。</param>
public readonly record struct FileWriteRecord(DateTimeOffset At, string Path, string Summary)
{
    /// <summary>时间的显示文本。</summary>
    public string TimeText => At.ToLocalTime().ToString(
        At.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss" : "MM-dd HH:mm");

    /// <summary>文件名(路径太长,历史列表里只放名字)。</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>文件树里的一项。</summary>
public sealed class FileEntryItem(ContainerFileEntry entry, string changeMarker) : ObservableObject
{
    /// <summary>底层条目。</summary>
    public ContainerFileEntry Entry { get; } = entry;

    /// <summary>名字。</summary>
    public string Name => Entry.Name;

    /// <summary>绝对路径。</summary>
    public string FullPath => Entry.FullPath;

    /// <summary>是否目录。</summary>
    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>图标资源键。</summary>
    public string Icon => Entry.IsDirectory ? "Icon.folder" : Entry.IsSymlink ? "Icon.link-2" : "Icon.file-text";

    /// <summary>大小文本。</summary>
    public string SizeText => Entry.IsDirectory ? "" : Humanize.Bytes(Entry.Size);

    /// <summary>权限。</summary>
    public string Mode => Entry.Mode;

    /// <summary>修改时间。</summary>
    public string Modified => Entry.Modified;

    /// <summary>符号链接的目标。</summary>
    public string LinkText => Entry.LinkTarget is { Length: > 0 } target ? $"→ {target}" : "";

    /// <summary>相对镜像的变更标记(A/C/D);没变过为空。</summary>
    public string ChangeMarker { get; } = changeMarker;

    /// <summary>有没有变更标记。</summary>
    public bool HasChange => ChangeMarker.Length > 0;

    /// <summary>「只看变更」过滤后是否显示。</summary>
    public bool Visible
    {
        get => _visible;
        set => SetField(ref _visible, value);
    }

    private bool _visible = true;

    /// <summary>变更标记的语气。</summary>
    public RowTone ChangeTone => ChangeMarker switch
    {
        "A" => RowTone.Ok,
        "D" => RowTone.Danger,
        "C" => RowTone.Warn,
        _ => RowTone.Idle
    };
}

/// <summary>
/// 容器内文件浏览与在线编辑。
/// <para>
/// 列目录走 exec 跑 <c>ls</c>(Engine API 没有列目录这个端点,而 <c>/archive</c>
/// 只能整包取走);读写单个文件走 <c>/archive</c> 的 tar 流 —— 不经 shell,
/// 因此不受登录 shell、引用规则与 locale 的影响。
/// </para>
/// </summary>
public sealed class ContainerFilesViewModel(DockerPanelViewModel shell, string containerId, string containerName)
    : ObservableObject
{
    private readonly Dictionary<string, string> _changes = [];
    private readonly List<FileWriteRecord> _history = [];
    private string _path = "/";
    private bool _loaded;
    private bool _busy;
    private string? _openFilePath;
    private string _editorText = "";
    private string _originalText = "";
    private string _error = "";
    private bool _changedOnly;
    private bool _diffMode;
    private bool _reloadAfterSave;
    private ContainerFileEntry? _openEntry;

    /// <summary>只看相对镜像有变更的那些条目。</summary>
    public bool ChangedOnly
    {
        get => _changedOnly;
        set
        {
            if (SetField(ref _changedOnly, value))
            {
                ApplyEntryView();
            }
        }
    }

    /// <summary>
    /// 差异视图(编辑 / 差异两态切换)。
    /// <para>
    /// 比的是**这次编辑前后**,不是相对镜像 —— 后者由左边那个 A/C/D 标记表达。
    /// 用户按下保存前想确认的是"我刚改了什么",而不是"这个文件相对镜像改过什么"。
    /// </para>
    /// </summary>
    public bool DiffMode
    {
        get => _diffMode;
        set
        {
            if (SetField(ref _diffMode, value))
            {
                OnPropertiesChanged(nameof(EditMode));
                RebuildEditorLines();
            }
        }
    }

    /// <summary>在编辑态(与 <see cref="DiffMode" /> 互斥)。</summary>
    public bool EditMode => !DiffMode;

    /// <summary>保存后顺带在容器里跑一次重载命令。</summary>
    public bool ReloadAfterSave
    {
        get => _reloadAfterSave;
        set => SetField(ref _reloadAfterSave, value);
    }

    /// <summary>差异视图里的行。</summary>
    public ObservableCollection<EditorLine> DiffLines { get; } = [];

    /// <summary>本面板往这个容器里写过的记录(最近在前)。</summary>
    public ObservableCollection<FileWriteRecord> History { get; } = [];

    /// <summary>有写入历史。</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>打开的那个文件的属性(大小 / 权限 / 属主 / 修改时间 / 相对镜像)。</summary>
    public ObservableCollection<DetailField> FileProperties { get; } = [];

    /// <summary>光标位置文本。</summary>
    public string CaretText
    {
        get => _caretText;
        private set => SetField(ref _caretText, value);
    }

    private string _caretText = "行 1, 列 1";

    /// <summary>已改了几行。</summary>
    public string ModifiedLinesText
    {
        get
        {
            if (!IsModified)
            {
                return "未修改";
            }
            int changed = LineDiff.CountChanged(LineDiff.Compute(_originalText, _editorText));
            return $"已修改 {changed} 行";
        }
    }

    /// <summary>换行符(照原文件的,保存时不改它)。</summary>
    public string LineEnding => _originalText.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : "LF";

    /// <summary>按扩展名猜的语言(只用来在状态条上显示)。</summary>
    public string Language => GuessLanguage(OpenFileName);

    /// <summary>
    /// 这个文件多半需要的重载命令;猜不出来时为空。
    /// <para>
    /// 只覆盖最常改的那几类配置。猜不到就不显示那个勾选框 ——
    /// 给一个会失败的默认命令,比不给更糟。
    /// </para>
    /// </summary>
    public string ReloadCommand => OpenFilePath is not { } path ? "" : path switch
    {
        var p when p.Contains("/nginx/", StringComparison.Ordinal) => "nginx -s reload",
        var p when p.Contains("/postgresql/", StringComparison.Ordinal) => "pg_ctl reload",
        var p when p.Contains("/redis", StringComparison.Ordinal) => "redis-cli CONFIG SET appendonly yes",
        _ => ""
    };

    /// <summary>有没有可用的重载命令。</summary>
    public bool HasReloadCommand => ReloadCommand.Length > 0;

    private static string GuessLanguage(string name)
    {
        int dot = name.LastIndexOf('.');
        string ext = dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
        return ext switch
        {
            "conf" or "cnf" or "ini" => name.Contains("nginx", StringComparison.OrdinalIgnoreCase) ? "nginx" : "ini",
            "yml" or "yaml" => "yaml",
            "json" => "json",
            "sh" or "bash" => "shell",
            "toml" => "toml",
            "xml" => "xml",
            "env" => "dotenv",
            "" => name.StartsWith('.') ? "dotfile" : "text",
            _ => ext
        };
    }

    /// <summary>当前目录。</summary>
    public string Path
    {
        get => _path;
        private set
        {
            if (SetField(ref _path, value))
            {
                OnPropertyChanged(nameof(CanGoUp));
            }
        }
    }

    /// <summary>能不能回上一级。</summary>
    public bool CanGoUp => Path != "/";

    /// <summary>当前目录下的条目。</summary>
    public ObservableCollection<FileEntryItem> Entries { get; } = [];

    /// <summary>正在读。</summary>
    public bool Busy
    {
        get => _busy;
        private set => SetField(ref _busy, value);
    }

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

    /// <summary>相对镜像变更了几项。</summary>
    public int ChangeCount => _changes.Count;

    /// <summary>变更计数文本。</summary>
    public string ChangeText => _changes.Count == 0 ? "与镜像一致" : $"相对镜像已变更 {_changes.Count} 项";

    /// <summary>正在编辑的文件路径;没打开时为 <see langword="null" />。</summary>
    public string? OpenFilePath
    {
        get => _openFilePath;
        private set
        {
            if (SetField(ref _openFilePath, value))
            {
                OnPropertiesChanged(nameof(HasOpenFile), nameof(OpenFileName), nameof(OpenFileDirectory));
            }
        }
    }

    /// <summary>有文件开着。</summary>
    public bool HasOpenFile => OpenFilePath is not null;

    /// <summary>打开的文件名。</summary>
    public string OpenFileName => OpenFilePath is { } p ? p[(p.LastIndexOf('/') + 1)..] : "";

    /// <summary>打开的文件所在目录。</summary>
    public string OpenFileDirectory => OpenFilePath is { } p && p.LastIndexOf('/') > 0 ? p[..(p.LastIndexOf('/') + 1)] : "/";

    /// <summary>编辑器内容。</summary>
    public string EditorText
    {
        get => _editorText;
        set
        {
            if (SetField(ref _editorText, value))
            {
                OnPropertiesChanged(nameof(IsModified), nameof(ModifiedText), nameof(ModifiedLinesText));
                RebuildEditorLines();
            }
        }
    }

    /// <summary>改过了没有。</summary>
    public bool IsModified => _editorText != _originalText;

    /// <summary>改动摘要。</summary>
    public string ModifiedText
    {
        get
        {
            if (!IsModified)
            {
                return "未修改";
            }
            int before = _originalText.Split('\n').Length;
            int after = _editorText.Split('\n').Length;
            int delta = after - before;
            return delta switch
            {
                > 0 => $"未保存 · +{delta} 行",
                < 0 => $"未保存 · {delta} 行",
                _ => "未保存"
            };
        }
    }

    /// <summary>打开一个目录或文件。</summary>
    public RelayCommand OpenCommand => _open ??= new(p => p is FileEntryItem item
        ? item.IsDirectory ? NavigateAsync(item.FullPath) : OpenFileAsync(item.FullPath)
        : Task.CompletedTask);

    private RelayCommand? _open;

    /// <summary>回上一级。</summary>
    public RelayCommand UpCommand => _up ??= new(_ =>
    {
        int slash = Path.TrimEnd('/').LastIndexOf('/');
        return NavigateAsync(slash <= 0 ? "/" : Path.TrimEnd('/')[..slash]);
    });

    private RelayCommand? _up;

    /// <summary>重新列一次当前目录。</summary>
    public RelayCommand RefreshCommand => _refresh ??= new(_ => NavigateAsync(Path));

    private RelayCommand? _refresh;

    /// <summary>关掉编辑器。</summary>
    public RelayCommand CloseFileCommand => _closeFile ??= new(_ =>
    {
        OpenFilePath = null;
        EditorText = "";
        _originalText = "";
    });

    private RelayCommand? _closeFile;

    /// <summary>撤销未保存的修改。</summary>
    public RelayCommand RevertCommand => _revert ??= new(_ => EditorText = _originalText);

    private RelayCommand? _revert;

    /// <summary>保存回容器。</summary>
    public RelayCommand SaveCommand => _save ??= new(_ => SaveAsync());

    private RelayCommand? _save;

    /// <summary>把一个文件或目录取到本地。</summary>
    public RelayCommand DownloadCommand => _download ??= new(p => p is FileEntryItem item
        ? DownloadAsync(item)
        : Task.CompletedTask);

    private RelayCommand? _download;

    /// <summary>把本地一个文件传进当前目录。</summary>
    public RelayCommand UploadCommand => _upload ??= new(_ => UploadAsync());

    private RelayCommand? _upload;

    /// <summary>能不能弹本地文件对话框(宿主没给顶层窗口时不能)。</summary>
    public bool CanPickFiles => FilePicker.IsAvailable;

    /// <summary>
    /// 取一份到本地。
    /// <para>
    /// 目录只能是 tar —— <c>/archive</c> 交出来的就是一个 tar 流,面板不替用户解包:
    /// 一个几万文件的目录在这里展开,失败到一半留下的半个目录树比什么都不做更糟。
    /// 单个文件则从 tar 里取出来还原成原样,因为用户要的是那个文件,不是一个装着它的盒子。
    /// </para>
    /// </summary>
    private async Task DownloadAsync(FileEntryItem item)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        string suggested = item.IsDirectory ? $"{item.Name}.tar" : item.Name;
        IStorageFile? target = await FilePicker
            .PickSaveAsync(item.IsDirectory ? $"把 {item.Name}/ 存成 tar" : $"保存 {item.Name}",
                suggested, item.IsDirectory ? "tar" : null)
            .ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            await using Stream archive = await client.DownloadArchiveAsync(containerId, item.FullPath, shell.Lifetime)
                                                     .ConfigureAwait(true);
            await using Stream output = await target.OpenWriteAsync().ConfigureAwait(true);
            long written;
            if (item.IsDirectory)
            {
                await archive.CopyToAsync(output, shell.Lifetime).ConfigureAwait(true);
                written = output.CanSeek ? output.Length : 0;
            }
            else
            {
                // 单文件走流式解 tar:整份读进内存的话,一个 2 GB 的日志会把宿主拖垮。
                written = await TarUtil.ExtractFirstFileAsync(archive, output, shell.Lifetime).ConfigureAwait(true);
            }
            shell.Feedback.Notify(FeedbackKind.Success, "已取到本地",
                $"{target.Name}{(written > 0 ? $" · {Humanize.Bytes(written)}" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
            shell.Feedback.ReportError("下载文件", ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>切到编辑态。</summary>
    public RelayCommand ShowEditCommand => _showEdit ??= new(_ =>
    {
        DiffMode = false;
        return Task.CompletedTask;
    });

    private RelayCommand? _showEdit;

    /// <summary>切到差异态。</summary>
    public RelayCommand ShowDiffCommand => _showDiff ??= new(_ =>
    {
        DiffMode = true;
        return Task.CompletedTask;
    });

    private RelayCommand? _showDiff;

    /// <summary>编辑器报一次光标位置(视图在选区变化时调)。</summary>
    public void ReportCaret(int line, int column) => CaretText = $"行 {line}, 列 {column}";

    /// <summary>按当前文本重算差异行。</summary>
    private void RebuildEditorLines()
    {
        DiffLines.Clear();
        if (!DiffMode)
        {
            return;
        }
        foreach (DiffLine line in LineDiff.Compute(_originalText, _editorText))
        {
            (string marker, RowTone tone) = line.Marker switch
            {
                DiffMarker.Added => ("+", RowTone.Ok),
                DiffMarker.Removed => ("−", RowTone.Danger),
                DiffMarker.Changed => ("~", RowTone.Warn),
                _ => (" ", RowTone.Idle)
            };
            // 删除的行在新文里没有行号,显示原文的那个 —— 空着会让人对不上原文件。
            int number = line.Marker == DiffMarker.Removed ? line.OldNumber : line.NewNumber;
            DiffLines.Add(new(number > 0 ? number.ToString() : "", marker, line.Text, tone));
        }
    }

    private void BuildFileProperties()
    {
        FileProperties.Clear();
        if (_openEntry is not { } entry)
        {
            return;
        }
        FileProperties.Add(new("大小", Humanize.Bytes(entry.Size)));
        FileProperties.Add(new("权限", entry.Mode));
        FileProperties.Add(new("属主", entry.Owner));
        FileProperties.Add(new("修改时间", entry.Modified));
        string marker = _changes.GetValueOrDefault(entry.FullPath, "");
        FileProperties.Add(marker switch
        {
            "A" => new("相对镜像", "新增 (A)", RowTone.Ok),
            "C" => new("相对镜像", "已修改 (C)", RowTone.Warn),
            "D" => new("相对镜像", "已删除 (D)", RowTone.Danger),
            _ => new("相对镜像", "与镜像一致")
        });
    }

    private void Record(string path, string summary)
    {
        // 只留在内存里:写入历史是"这次会话里我动过什么"的备忘,
        // 落盘会把它变成一份需要清理、需要考虑隐私的东西,而它不值那个代价。
        _history.Insert(0, new(DateTimeOffset.UtcNow, path, summary));
        while (_history.Count > 20)
        {
            _history.RemoveAt(_history.Count - 1);
        }
        History.Clear();
        foreach (FileWriteRecord record in _history)
        {
            History.Add(record);
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>这次改动的摘要,进写入历史(<c>+2 −1</c> 这种)。</summary>
    private string DescribeEdit()
    {
        IReadOnlyList<DiffLine> diff = LineDiff.Compute(_originalText, _editorText);
        int added = diff.Count(l => l.Marker is DiffMarker.Added or DiffMarker.Changed);
        int removed = diff.Count(l => l.Marker is DiffMarker.Removed or DiffMarker.Changed);
        return _originalText.Length == 0 ? "新建"
            : added == 0 && removed == 0 ? "无改动"
            : $"+{added} −{removed}";
    }

    /// <summary>
    /// 保存后跑一次重载命令。
    /// <para>
    /// 失败**不**回滚文件 —— 文件已经写进去了,那是既成事实;
    /// 假装"保存失败"会让用户以为可以重来一次,而实际上容器里的内容已经变了。
    /// </para>
    /// </summary>
    private async Task RunReloadAsync(DockerClient client)
    {
        string command = ReloadCommand;
        try
        {
            ExecCapture result = await client
                .ExecCaptureAsync(containerId, ["/bin/sh", "-c", command], cancellationToken: shell.Lifetime)
                .ConfigureAwait(true);
            if (result.ExitCode == 0)
            {
                shell.Feedback.Notify(FeedbackKind.Success, "已重载配置", $"{containerName}:{command}");
                return;
            }
            shell.Feedback.Notify(FeedbackKind.Warning, "文件已写回,但重载失败",
                $"{command} 退出码 {result.ExitCode}\n{result.StandardError.Trim()}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            shell.Feedback.Notify(FeedbackKind.Warning, "文件已写回,但重载没跑成", $"{command}:{ex.Message}");
        }
    }

    private void ApplyEntryView()
    {
        foreach (FileEntryItem item in Entries)
        {
            // 目录永远留着 —— 藏掉目录会让"只看变更"变成一个走不进任何子目录的死胡同。
            item.Visible = !_changedOnly || item.IsDirectory || item.HasChange;
        }
    }

    /// <summary>把本地一个文件送进容器当前目录。</summary>
    /// <summary>接住拖进来的那个文件,当成上传到当前目录。</summary>
    public Task UploadDroppedAsync(IStorageFile file) => UploadFileAsync(file);

    private async Task UploadAsync()
    {
        IStorageFile? source = await FilePicker.PickOpenAsync($"上传到 {Path}").ConfigureAwait(true);
        if (source is not null)
        {
            await UploadFileAsync(source).ConfigureAwait(true);
        }
    }

    private async Task UploadFileAsync(IStorageFile source)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        byte[] content;
        await using (Stream input = await source.OpenReadAsync().ConfigureAwait(true))
        {
            if (input.CanSeek && input.Length > DockerClient.MaxUploadFileBytes)
            {
                Error = $"{source.Name} 有 {Humanize.Bytes(input.Length)},超过单文件上传上限 " +
                        $"{Humanize.Bytes(DockerClient.MaxUploadFileBytes)}。大文件请用 scp 之类的通道。";
                return;
            }
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, shell.Lifetime).ConfigureAwait(true);
            content = buffer.ToArray();
        }
        string target = Path.TrimEnd('/') + "/" + source.Name;
        bool exists = Entries.Any(e => e.Name == source.Name);
        bool confirmed = await shell.Confirm.AskAsync(shell.BuildConfirm(new()
        {
            Title = exists ? $"覆盖容器内的 {source.Name}?" : $"上传 {source.Name} 到 {containerName}?",
            Icon = exists ? "Docker.shield-alert" : "Icon.upload",
            // 覆盖已有文件是不可撤销的,要手打确认串;新建一个文件不是,点一下就够。
            Tier = exists ? ConfirmTier.DataLoss : ConfirmTier.Destructive,
            ConfirmWord = "overwrite",
            ConfirmLabel = exists ? "覆盖" : "上传",
            ConfirmIcon = "Icon.upload",
            HostName = "",
            Commands = [$"docker cp {source.Name} {containerName}:{target}"],
            CommandNote = $"{Humanize.Bytes(content.Length)},以 tar 流写入容器可写层,不经过 shell。",
            DataLossHeadline = exists ? "容器里同名的那个文件会被整体覆盖" : "",
            DataLossPoints = exists
                ?
                [
                    "Docker 不做备份,也没有回收站。",
                    "改动只在容器的可写层里:容器一旦被删除或重建,它就没了。"
                ]
                : []
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            await client.WriteFileAsync(containerId, target, content, shell.Lifetime).ConfigureAwait(true);
            shell.Feedback.Notify(FeedbackKind.Success, "已上传", $"{target} · {Humanize.Bytes(content.Length)}");
            await LoadChangesAsync().ConfigureAwait(true);
            await NavigateAsync(Path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
            shell.Feedback.ReportError("上传文件", ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 直接跳到某个目录(卷页"浏览卷内文件"用:它已经知道卷挂在容器的哪儿了,
    /// 让用户从 <c>/</c> 一级一级点下去是白费一遍功夫)。
    /// </summary>
    public async Task GoToAsync(string path)
    {
        if (!_loaded)
        {
            _loaded = true;
            await LoadChangesAsync().ConfigureAwait(true);
        }
        await NavigateAsync(path).ConfigureAwait(true);
    }

    /// <summary>第一次进这一页时才加载。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await LoadChangesAsync().ConfigureAwait(true);
        await NavigateAsync("/").ConfigureAwait(true);
    }

    private async Task LoadChangesAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        try
        {
            FilesystemChange[] changes = await client.ChangesAsync(containerId, shell.Lifetime).ConfigureAwait(true);
            _changes.Clear();
            foreach (FilesystemChange change in changes)
            {
                _changes[change.Path] = change.Marker;
            }
            OnPropertiesChanged(nameof(ChangeCount), nameof(ChangeText));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拿不到 diff 不该挡住文件浏览 —— 标记只是锦上添花。
            shell.Context.Log.Debug($"docker diff failed: {ex.Message}");
        }
    }

    private async Task NavigateAsync(string path)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            ContainerFileEntry[] entries = await client.ListDirectoryAsync(containerId, path, shell.Lifetime)
                                                       .ConfigureAwait(true);
            Path = path;
            Entries.Clear();
            foreach (ContainerFileEntry entry in entries)
            {
                Entries.Add(new(entry, _changes.GetValueOrDefault(entry.FullPath, "")));
            }
            ApplyEntryView();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenFileAsync(string path)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            byte[] bytes = await client.ReadFileAsync(containerId, path, DockerClient.MaxEditableFileBytes, shell.Lifetime)
                                       .ConfigureAwait(true);
            if (LooksBinary(bytes))
            {
                Error = $"{path} 看起来是二进制文件 —— 面板只在线编辑文本。";
                return;
            }
            _originalText = Encoding.UTF8.GetString(bytes);
            EditorText = _originalText;
            OpenFilePath = path;
            _openEntry = Entries.FirstOrDefault(e => e.FullPath == path)?.Entry;
            DiffMode = false;
            BuildFileProperties();
            OnPropertiesChanged(nameof(ModifiedLinesText), nameof(LineEnding), nameof(Language),
                nameof(ReloadCommand), nameof(HasReloadCommand));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 二进制探测:前 8 KB 里出现 NUL 就当二进制。
    /// <para>
    /// 把一个 ELF 塞进文本编辑器,用户点一次保存就把它毁了 ——
    /// 而"毁了"这件事要等到容器下次启动失败才会被发现。
    /// </para>
    /// </summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        int limit = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private async Task SaveAsync()
    {
        if (shell.Client is not { } client || OpenFilePath is not { } path || !IsModified)
        {
            return;
        }
        bool confirmed = await shell.Confirm.AskAsync(shell.BuildConfirm(new()
        {
            Title = $"覆盖容器内的 {OpenFileName}?",
            Icon = "Docker.shield-alert",
            Tier = ConfirmTier.DataLoss,
            ConfirmWord = "save",
            ConfirmLabel = "写回容器",
            ConfirmIcon = "Icon.save",
            HostName = "",
            Commands = [$"PUT /containers/{containerName}/archive?path={OpenFileDirectory}"],
            CommandNote = "整个文件以 tar 流写回容器可写层,不经过 shell。",
            DataLossHeadline = "原文件会被整体覆盖,无法撤销",
            DataLossPoints =
            [
                "Docker 不做备份,也没有回收站 —— 写回去就是写回去了。",
                "改动只在容器的可写层里:容器一旦被删除或重建,它就没了。",
                "多数服务不会自动重载配置,保存后多半还要 reload 或重启容器。"
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        Busy = true;
        try
        {
            await client.WriteFileAsync(containerId, path, Encoding.UTF8.GetBytes(EditorText), shell.Lifetime)
                        .ConfigureAwait(true);
            Record(path, DescribeEdit());
            _originalText = EditorText;
            OnPropertiesChanged(nameof(IsModified), nameof(ModifiedText), nameof(ModifiedLinesText));
            RebuildEditorLines();
            shell.Feedback.Notify(FeedbackKind.Success, "已写回容器", $"{path} · {Humanize.Bytes(Encoding.UTF8.GetByteCount(EditorText))}");
            await LoadChangesAsync().ConfigureAwait(true);
            if (ReloadAfterSave && HasReloadCommand)
            {
                await RunReloadAsync(client).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            shell.Feedback.ReportError("写回文件", ex);
        }
        finally
        {
            Busy = false;
        }
    }
}
