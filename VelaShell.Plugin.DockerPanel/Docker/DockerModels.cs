namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 面板里的一行数据。
/// <para>
/// 这些类型刻意是**只读记录**而不是可观察对象:列表每次刷新整批换新,
/// 逐字段通知反而要为"这一行还是不是上次那一行"维护一套身份逻辑。
/// 选中项的保持由视图模型按 id 重连(见 <c>DockerPanelViewModel.Reselect</c>)。
/// </para>
/// </summary>
public static class DockerLabels
{
    /// <summary>compose 写在容器上的项目名标签。</summary>
    public const string ComposeProject = "com.docker.compose.project";

    /// <summary>compose 写在容器上的服务名标签。</summary>
    public const string ComposeService = "com.docker.compose.service";

    /// <summary>compose 写在容器上的工作目录标签(<c>docker compose</c> 据此找 compose 文件)。</summary>
    public const string ComposeWorkingDir = "com.docker.compose.project.working_dir";

    /// <summary>从 <c>docker ps</c> 的 Labels 串(<c>a=b,c=d</c>)里取一个标签值。</summary>
    /// <param name="labels">标签串。</param>
    /// <param name="key">标签名。</param>
    /// <returns>标签值;没有返回空串。</returns>
    public static string Get(string labels, string key)
    {
        if (labels.Length == 0)
        {
            return string.Empty;
        }
        foreach (string pair in labels.Split(','))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0 && pair.AsSpan(0, equals).Trim().SequenceEqual(key))
            {
                return pair[(equals + 1)..].Trim();
            }
        }
        return string.Empty;
    }
}

/// <summary>一个容器。字段与 <c>docker ps --format '{{json .}}'</c> 一一对应。</summary>
public sealed record ContainerItem
{
    /// <summary>完整容器 id。</summary>
    public required string Id { get; init; }

    /// <summary>容器名(多名时 docker 以逗号分隔)。</summary>
    public required string Name { get; init; }

    /// <summary>镜像引用。</summary>
    public string Image { get; init; } = string.Empty;

    /// <summary>启动命令。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>创建时间(远端本地时间字符串)。</summary>
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>已运行时长的人话形式(如 <c>3 days ago</c>)。</summary>
    public string RunningFor { get; init; } = string.Empty;

    /// <summary>端口映射串。</summary>
    public string Ports { get; init; } = string.Empty;

    /// <summary>状态机状态(<c>running</c> / <c>exited</c> / <c>paused</c> …)。</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>状态描述(如 <c>Up 3 days (healthy)</c>)。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>可写层大小(仅在 <c>docker ps -s</c> 时有值)。</summary>
    public string Size { get; init; } = string.Empty;

    /// <summary>所属网络。</summary>
    public string Networks { get; init; } = string.Empty;

    /// <summary>挂载。</summary>
    public string Mounts { get; init; } = string.Empty;

    /// <summary>标签串。</summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>短 id(界面上够用,也是 docker 自己的展示形式)。</summary>
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;

    /// <summary>是否在跑。</summary>
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否被暂停。</summary>
    public bool IsPaused => State.Equals("paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否已退出(含 created / dead)。</summary>
    public bool IsStopped => !IsRunning && !IsPaused;

    /// <summary>健康检查报告的不健康状态(docker 把它写在 Status 里)。</summary>
    public bool IsUnhealthy => Status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase);

    /// <summary>所属 compose 项目名;不是 compose 起的容器为空串。</summary>
    public string ComposeProject => DockerLabels.Get(Labels, DockerLabels.ComposeProject);

    /// <summary>compose 服务名;非 compose 容器为空串。</summary>
    public string ComposeService => DockerLabels.Get(Labels, DockerLabels.ComposeService);

    /// <summary>是 compose 起的容器(界面据此决定要不要在名字下面补一行项目名)。</summary>
    public bool HasComposeProject => ComposeProject.Length > 0;

    /// <summary>端口列显示用:只保留有映射的部分,一屏塞不下四十个端口。</summary>
    public string PortsDisplay
    {
        get
        {
            if (Ports.Length == 0)
            {
                return string.Empty;
            }
            // "0.0.0.0:8080->80/tcp, :::8080->80/tcp" —— IPv6 那半条是同一个映射的另一面,
            // 两条都列出来只会把列撑爆,这里只留能读出"外部端口→内部端口"的那些。
            List<string> mapped = [];
            foreach (string part in Ports.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Contains("->", StringComparison.Ordinal) && !trimmed.StartsWith(":::", StringComparison.Ordinal))
                {
                    mapped.Add(trimmed);
                }
            }
            return mapped.Count > 0 ? string.Join(", ", mapped) : Ports;
        }
    }
}

/// <summary>一个镜像。字段与 <c>docker images --format '{{json .}}'</c> 对应。</summary>
public sealed record ImageItem
{
    /// <summary>镜像 id(可能带 <c>sha256:</c> 前缀)。</summary>
    public required string Id { get; init; }

    /// <summary>仓库名。</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>标签。</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>摘要。</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>创建时间。</summary>
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>创建至今(人话)。</summary>
    public string CreatedSince { get; init; } = string.Empty;

    /// <summary>大小。</summary>
    public string Size { get; init; } = string.Empty;

    /// <summary>短 id。</summary>
    public string ShortId
    {
        get
        {
            string id = Id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Id[7..] : Id;
            return id.Length > 12 ? id[..12] : id;
        }
    }

    /// <summary>悬空镜像(仓库与标签都是 <c>&lt;none&gt;</c>)—— 删起来最安全的那一类。</summary>
    public bool IsDangling =>
        Repository is "<none>" or "" || Tag is "<none>" or "";

    /// <summary>可直接交给 docker 的引用:有标签用 <c>repo:tag</c>,悬空的只能用 id。</summary>
    public string Reference => IsDangling ? ShortId : $"{Repository}:{Tag}";

    /// <summary>列表显示名。</summary>
    public string Display => IsDangling ? $"<none>:<none>" : $"{Repository}:{Tag}";
}

/// <summary>一个卷。字段与 <c>docker volume ls --format '{{json .}}'</c> 对应。</summary>
public sealed record VolumeItem
{
    /// <summary>卷名。</summary>
    public required string Name { get; init; }

    /// <summary>驱动。</summary>
    public string Driver { get; init; } = string.Empty;

    /// <summary>作用域。</summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>宿主机挂载点。</summary>
    public string Mountpoint { get; init; } = string.Empty;

    /// <summary>大小(仅 <c>docker system df -v</c> 有;<c>volume ls</c> 通常为 N/A)。</summary>
    public string Size { get; init; } = string.Empty;

    /// <summary>引用它的容器数(部分版本提供)。</summary>
    public string Links { get; init; } = string.Empty;

    /// <summary>标签串。</summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>所属 compose 项目名。</summary>
    public string ComposeProject => DockerLabels.Get(Labels, DockerLabels.ComposeProject);
}

/// <summary>一个网络。字段与 <c>docker network ls --format '{{json .}}'</c> 对应。</summary>
public sealed record NetworkItem
{
    /// <summary>网络 id。</summary>
    public required string Id { get; init; }

    /// <summary>网络名。</summary>
    public required string Name { get; init; }

    /// <summary>驱动。</summary>
    public string Driver { get; init; } = string.Empty;

    /// <summary>作用域。</summary>
    public string Scope { get; init; } = string.Empty;

    /// <summary>是否内部网络。</summary>
    public string Internal { get; init; } = string.Empty;

    /// <summary>是否启用 IPv6。</summary>
    public string IPv6 { get; init; } = string.Empty;

    /// <summary>创建时间。</summary>
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>标签串。</summary>
    public string Labels { get; init; } = string.Empty;

    /// <summary>短 id。</summary>
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;

    /// <summary>docker 自带、删不掉的三张网。界面据此把"删除"灰掉,而不是让用户撞一次错误。</summary>
    public bool IsBuiltIn => Name is "bridge" or "host" or "none";

    /// <summary>所属 compose 项目名。</summary>
    public string ComposeProject => DockerLabels.Get(Labels, DockerLabels.ComposeProject);
}

/// <summary>一个 compose 项目。</summary>
public sealed record ComposeProjectItem
{
    /// <summary>项目名。</summary>
    public required string Name { get; init; }

    /// <summary>状态串(如 <c>running(3)</c>、<c>exited(1), running(2)</c>)。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>compose 文件路径(多份以逗号分隔)。</summary>
    public string ConfigFiles { get; init; } = string.Empty;

    /// <summary>第一份 compose 文件路径 —— 编辑与"按文件操作"都对着它。</summary>
    public string PrimaryConfigFile
    {
        get
        {
            if (ConfigFiles.Length == 0)
            {
                return string.Empty;
            }
            int comma = ConfigFiles.IndexOf(',');
            return (comma > 0 ? ConfigFiles[..comma] : ConfigFiles).Trim();
        }
    }

    /// <summary>项目里至少有一个服务在跑。</summary>
    public bool IsRunning => Status.Contains("running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一个容器的实时统计。字段与 <c>docker stats --format '{{json .}}'</c> 对应。</summary>
public sealed record StatsItem
{
    /// <summary>容器 id。</summary>
    public required string Id { get; init; }

    /// <summary>容器名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>CPU 占用(如 <c>1.23%</c>)。</summary>
    public string CpuPercent { get; init; } = string.Empty;

    /// <summary>内存用量(如 <c>120MiB / 2GiB</c>)。</summary>
    public string MemUsage { get; init; } = string.Empty;

    /// <summary>内存占比。</summary>
    public string MemPercent { get; init; } = string.Empty;

    /// <summary>网络收发。</summary>
    public string NetIO { get; init; } = string.Empty;

    /// <summary>块设备读写。</summary>
    public string BlockIO { get; init; } = string.Empty;

    /// <summary>进程数。</summary>
    public string Pids { get; init; } = string.Empty;

    /// <summary>CPU 百分比的数值形式(排序与进度条用);解析不出为 0。</summary>
    public double CpuValue => ParsePercent(CpuPercent);

    /// <summary>内存百分比的数值形式;解析不出为 0。</summary>
    public double MemValue => ParsePercent(MemPercent);

    private static double ParsePercent(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim().TrimEnd('%');
        return double.TryParse(span, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
    }
}
