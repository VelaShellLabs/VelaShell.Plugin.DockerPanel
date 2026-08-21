namespace VelaShell.Plugin.DockerPanel.Docker;

internal sealed partial class DockerApi
{
    /// <summary>拼出列容器的那条命令(快照与单独刷新共用,免得两处慢慢长歪)。</summary>
    /// <param name="all">连已停止的一起列。</param>
    /// <param name="withSize">连可写层大小一起要。</param>
    /// <returns>命令行。</returns>
    private string ListContainersCommand(bool all, bool withSize) =>
        $"{D} ps{(all ? " -a" : "")}{(withSize ? " -s" : "")} --no-trunc --format '{{{{json .}}}}'";

    /// <summary>列出容器。</summary>
    /// <param name="all">连已停止的一起列(<c>-a</c>)。</param>
    /// <param name="withSize">连可写层大小一起要(<c>-s</c>);它在容器多时明显更慢,默认关。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>容器列表与原始输出(解析不出时界面要能把原文摆给用户看)。</returns>
    public async Task<(IReadOnlyList<ContainerItem> Items, DockerResult Result)> ListContainersAsync(
        bool all, bool withSize, CancellationToken cancellationToken)
    {
        DockerResult result = await Engine.RunAsync(ListContainersCommand(all, withSize), null, cancellationToken).ConfigureAwait(false);
        return (ParseContainers(result.Output), result);
    }

    /// <summary>
    /// 容器页一次刷新要的**全部**东西:列表 + 头部计数 + CPU/内存,**一次**往返拿回来。
    /// <para>
    /// 分三次调用在本机看不出差别,在一条 200ms 往返的跨洋链路上就是"每 5 秒卡 0.6 秒"。
    /// §9 的"探测类命令合并执行"说的正是这件事。
    /// </para>
    /// </summary>
    /// <param name="all">连已停止的容器一起列。</param>
    /// <param name="withSize">连可写层大小一起要。</param>
    /// <param name="withStats">要不要 CPU / 内存(它是这几段里最慢的一段)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>列表、计数与按短 id 索引的统计。</returns>
    public async Task<ContainerSnapshot> SnapshotContainersAsync(
        bool all, bool withSize, bool withStats, CancellationToken cancellationToken)
    {
        List<string> commands =
        [
            ListContainersCommand(all, withSize),
            $"{D} ps -aq | wc -l",
            $"{D} ps -q | wc -l",
            $"{D} images -q | wc -l",
            $"{D} volume ls -q | wc -l"
        ];
        if (withStats)
        {
            commands.Add($"{D} stats --no-stream --format '{{{{json .}}}}'");
        }
        IReadOnlyList<string> sections =
            await Engine.RunSectionsAsync(commands, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContainerItem> items = ParseContainers(sections.ElementAtOrDefault(0) ?? string.Empty);
        return new(
            items,
            new(ParseCount(sections, 1), ParseCount(sections, 2), ParseCount(sections, 3), ParseCount(sections, 4)),
            withStats ? ParseStats(sections.ElementAtOrDefault(5) ?? string.Empty) : new Dictionary<string, StatsItem>());
    }

    private static IReadOnlyList<ContainerItem> ParseContainers(string output)
    {
        List<ContainerItem> items = [];
        foreach (IReadOnlyDictionary<string, string> row in DockerJson.ParseLines(output))
        {
            items.Add(new()
            {
                Id = DockerJson.Str(row, "ID"),
                Name = DockerJson.Str(row, "Names"),
                Image = DockerJson.Str(row, "Image"),
                Command = DockerJson.Str(row, "Command").Trim('"'),
                CreatedAt = DockerJson.Str(row, "CreatedAt"),
                RunningFor = DockerJson.Str(row, "RunningFor"),
                Ports = DockerJson.Str(row, "Ports"),
                State = DockerJson.Str(row, "State"),
                Status = DockerJson.Str(row, "Status"),
                Size = DockerJson.Str(row, "Size"),
                Networks = DockerJson.Str(row, "Networks"),
                Mounts = DockerJson.Str(row, "Mounts"),
                Labels = DockerJson.Str(row, "Labels")
            });
        }
        // docker 按创建时间倒序回,已经是最想要的顺序;但停掉的容器混在中间不好扫,
        // 把在跑的提到前面(同组内保持 docker 的顺序 —— OrderBy 是稳定排序)。
        return [.. items.OrderByDescending(static c => c.IsRunning)];
    }

    /// <summary>容器生命周期动作(start / stop / restart / pause / unpause / kill)。</summary>
    /// <param name="action">docker 子命令。</param>
    /// <param name="ids">容器 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个容器的结果。</returns>
    public Task<IReadOnlyList<BatchOutcome>> ContainerActionAsync(
        string action, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        RunBatchAsync(ids, id => $"{D} {action} {Sh.Quote(id)}", LifecycleTimeout, cancellationToken);

    /// <summary>删除容器。</summary>
    /// <param name="ids">容器 id。</param>
    /// <param name="force">在跑也删(<c>-f</c>)。</param>
    /// <param name="removeVolumes">连匿名卷一起删(<c>-v</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个容器的结果。</returns>
    public Task<IReadOnlyList<BatchOutcome>> RemoveContainersAsync(
        IReadOnlyList<string> ids, bool force, bool removeVolumes, CancellationToken cancellationToken) =>
        RunBatchAsync(ids,
            id => $"{D} rm{(force ? " -f" : "")}{(removeVolumes ? " -v" : "")} {Sh.Quote(id)}",
            LifecycleTimeout, cancellationToken);

    /// <summary>重命名容器。</summary>
    /// <param name="id">容器 id。</param>
    /// <param name="newName">新名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<DockerResult> RenameContainerAsync(string id, string newName, CancellationToken cancellationToken) =>
        Engine.RunAsync($"{D} rename {Sh.Quote(id)} {Sh.Quote(newName)}", LifecycleTimeout, cancellationToken);

    /// <summary>改容器的重启策略(<c>docker update --restart</c>)。</summary>
    /// <param name="ids">容器 id。</param>
    /// <param name="policy">策略(no / on-failure / always / unless-stopped)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个容器的结果。</returns>
    public Task<IReadOnlyList<BatchOutcome>> UpdateRestartPolicyAsync(
        IReadOnlyList<string> ids, string policy, CancellationToken cancellationToken) =>
        RunBatchAsync(ids, id => $"{D} update --restart={Sh.Quote(policy)} {Sh.Quote(id)}", LifecycleTimeout, cancellationToken);

    /// <summary>容器详情(inspect 的完整 JSON)。</summary>
    /// <param name="id">容器 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的 JSON,或 docker 的错误文本。</returns>
    public async Task<string> InspectContainerAsync(string id, CancellationToken cancellationToken)
    {
        DockerResult result = await Engine.RunAsync($"{D} inspect {Sh.Quote(id)}", null, cancellationToken).ConfigureAwait(false);
        return result.Ok ? DockerJson.Pretty(result.Output) : result.Output;
    }

    /// <summary>容器内的进程表(<c>docker top</c>)。</summary>
    /// <param name="id">容器 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原始表格文本。</returns>
    public async Task<string> TopAsync(string id, CancellationToken cancellationToken)
    {
        DockerResult result = await Engine.RunAsync($"{D} top {Sh.Quote(id)} aux", null, cancellationToken).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>容器相对镜像的文件系统改动(<c>docker diff</c>)。</summary>
    /// <param name="id">容器 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原始文本(最多 2000 行:改动上万的容器不该把界面拖死)。</returns>
    public async Task<string> DiffAsync(string id, CancellationToken cancellationToken)
    {
        DockerResult result = await Engine.RunAsync($"{D} diff {Sh.Quote(id)}", null, cancellationToken).ConfigureAwait(false);
        return OutputText.Tail(result.Output, 2000);
    }

    /// <summary>容器的端口映射(<c>docker port</c>)。</summary>
    /// <param name="id">容器 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原始文本。</returns>
    public async Task<string> PortsAsync(string id, CancellationToken cancellationToken)
    {
        DockerResult result = await Engine.RunAsync($"{D} port {Sh.Quote(id)}", null, cancellationToken).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>
    /// 取日志。
    /// <para>
    /// **不是 <c>-f</c>**:远程执行能力是一次性的,<c>docker logs -f</c> 永远不返回,
    /// 只会挂到超时然后把整段丢掉。这里取一次快照,"跟随"由面板按固定间隔用
    /// <paramref name="since" /> 增量续取实现(见 <c>DockerPanelViewModel.Logs</c>)。
    /// </para>
    /// </summary>
    /// <param name="id">容器 id。</param>
    /// <param name="tail">末尾行数;<c>0</c> 表示不限(配合 since 用)。</param>
    /// <param name="timestamps">是否带时间戳(增量续取必须带,否则续不上)。</param>
    /// <param name="since">起始时间(RFC3339 或 <c>10m</c> 这样的相对量);为空不限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>日志文本。</returns>
    public async Task<DockerResult> LogsAsync(
        string id, int tail, bool timestamps, string since, CancellationToken cancellationToken)
    {
        string command = $"{D} logs{(timestamps ? " --timestamps" : "")}";
        if (tail > 0)
        {
            command += $" --tail {tail}";
        }
        if (since.Length > 0)
        {
            command += $" --since {Sh.Quote(since)}";
        }
        command += $" {Sh.Quote(id)}";
        DockerResult result = await Engine.RunAsync(command, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return result with { Output = OutputText.Collapse(result.Output) };
    }

    /// <summary>取一次全量统计快照(<c>--no-stream</c>,一次往返拿到所有容器)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按短 id 索引的统计。</returns>
    public async Task<IReadOnlyDictionary<string, StatsItem>> StatsAsync(CancellationToken cancellationToken)
    {
        DockerResult result = await Engine
                                    .RunAsync($"{D} stats --no-stream --format '{{{{json .}}}}'", TimeSpan.FromSeconds(60), cancellationToken)
                                    .ConfigureAwait(false);
        return ParseStats(result.Output);
    }

    private static IReadOnlyDictionary<string, StatsItem> ParseStats(string output)
    {
        Dictionary<string, StatsItem> stats = new(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string> row in DockerJson.ParseLines(OutputText.Collapse(output)))
        {
            StatsItem item = new()
            {
                Id = DockerJson.Str(row, "ID"),
                Name = DockerJson.Str(row, "Name"),
                CpuPercent = DockerJson.Str(row, "CPUPerc"),
                MemUsage = DockerJson.Str(row, "MemUsage"),
                MemPercent = DockerJson.Str(row, "MemPerc"),
                NetIO = DockerJson.Str(row, "NetIO"),
                BlockIO = DockerJson.Str(row, "BlockIO"),
                Pids = DockerJson.Str(row, "PIDs")
            };
            if (item.Id.Length > 0)
            {
                // stats 回的是短 id,而 ps --no-trunc 回的是长 id:两边按短 id 对齐。
                stats[item.Id] = item;
            }
        }
        return stats;
    }

    /// <summary>
    /// 生成"进容器"的那条命令。
    /// <para>
    /// 面板**不自己开伪终端** —— 它把这条命令写进用户当前的终端标签(需用户授权),
    /// 于是补全、快捷键、会话日志、录制、ZMODEM 全都是宿主终端原本那一套。
    /// 插件自己另起一套交互式通道只会做出一个更差的终端。
    /// </para>
    /// </summary>
    /// <param name="id">容器 id。</param>
    /// <param name="shell">要用的 shell(<c>bash</c> / <c>sh</c> / …)。</param>
    /// <param name="user">以哪个用户进入;为空用镜像默认。</param>
    /// <param name="workdir">工作目录;为空用镜像默认。</param>
    /// <returns>可直接键入终端的一行命令(**不含**换行)。</returns>
    public string BuildExecCommand(string id, string shell, string user, string workdir)
    {
        string command = $"{D} exec -it";
        if (user.Length > 0)
        {
            command += $" -u {Sh.Quote(user)}";
        }
        if (workdir.Length > 0)
        {
            command += $" -w {Sh.Quote(workdir)}";
        }
        // 常见镜像(alpine、distroless 派生)里没有 bash;先试再退回 sh,
        // 比让用户吃一句 "executable file not found" 再自己改一遍强。
        string safeShell = shell.Length > 0 ? shell : "bash";
        return $"{command} {Sh.Quote(id)} {safeShell} 2>/dev/null || {command} {Sh.Quote(id)} sh";
    }
}

/// <summary>头部那一行的四个计数;取不到的为 -1。</summary>
/// <param name="Containers">容器总数。</param>
/// <param name="Running">在跑的容器数。</param>
/// <param name="Images">镜像数。</param>
/// <param name="Volumes">卷数。</param>
internal sealed record ContainerCounts(int Containers, int Running, int Images, int Volumes);

/// <summary>容器页一次刷新的全部结果。</summary>
/// <param name="Containers">容器列表。</param>
/// <param name="Counts">头部计数。</param>
/// <param name="Stats">按短 id 索引的实时统计;没要统计时为空字典。</param>
internal sealed record ContainerSnapshot(
    IReadOnlyList<ContainerItem> Containers,
    ContainerCounts Counts,
    IReadOnlyDictionary<string, StatsItem> Stats);
