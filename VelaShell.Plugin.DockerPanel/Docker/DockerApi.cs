namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 面板用得到的全部 docker 操作,按域拆成几个 partial 文件
/// (<c>.Containers</c> / <c>.Images</c> / <c>.VolumesNetworks</c> / <c>.Compose</c> / <c>.System</c>)。
/// <para>
/// 这一层只做两件事:**拼命令**与**解析输出**。它不认识界面,也不持有状态 ——
/// 所以命令拼装可以在单测里逐条比对(见 <c>DockerApiCommandTests</c>),
/// 而不必起一个 Avalonia 应用。
/// </para>
/// <para>
/// 超时分三档:列表与探测 30 秒(默认)、单个容器的生命周期操作 2 分钟
/// (<c>docker stop</c> 默认给容器 10 秒优雅退出,批量选十个就要一分半)、
/// 拉镜像与 compose 起停 10 分钟(宿主的上限)。
/// </para>
/// </summary>
/// <param name="engine">绑定了会话的执行引擎。</param>
internal sealed partial class DockerApi(DockerEngine engine)
{
    /// <summary>生命周期类操作的超时。</summary>
    public static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>拉取 / 构建 / compose 起停的超时(宿主允许的上限)。</summary>
    public static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(10);

    /// <summary>底层引擎(会话、sudo、探测结果)。</summary>
    public DockerEngine Engine { get; } = engine;

    /// <summary>docker 命令前缀。</summary>
    private string D => Engine.DockerPrefix;

    /// <summary>
    /// 对一批目标逐个执行同一条命令,并把每个目标的成败分别记下来。
    /// <para>
    /// **为什么不是 <c>docker stop a b c</c>**:docker 确实接受多个目标,但只回一个退出码 ——
    /// 停了九个、第十个失败,用户看到的是"失败",而那九个已经停了。
    /// 这里一条一条跑(仍然是**一次** exec 往返,靠 <c>;</c> 串起来),
    /// 每条自己带哨兵,于是界面能说清"8 成功 / 2 失败:xxx 正被 yyy 依赖"。
    /// </para>
    /// </summary>
    /// <param name="targets">目标(容器 id、镜像引用、卷名…)。</param>
    /// <param name="build">按目标生成命令。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个目标的结果。</returns>
    public async Task<IReadOnlyList<BatchOutcome>> RunBatchAsync(
        IReadOnlyList<string> targets,
        Func<string, string> build,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return [];
        }
        IReadOnlyList<string> sections =
            await Engine.RunSectionsAsync([.. targets.Select(build)], timeout, cancellationToken).ConfigureAwait(false);
        List<BatchOutcome> outcomes = new(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            string output = OutputText.Collapse(sections.ElementAtOrDefault(i) ?? string.Empty).Trim();
            // 分段执行拿不到每段的退出码(哨兵只有一个,在整条脚本的末尾)。
            // 判据换成 docker 自己的行为:成功时它只回显目标名/id,失败时输出里带
            // "Error response from daemon" 或 "Error:"。这比再跑 N 次往返划算得多。
            bool failed = output.Contains("Error response from daemon", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("Error:", StringComparison.OrdinalIgnoreCase)
                          || output.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("No such container", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("not found", StringComparison.OrdinalIgnoreCase);
            outcomes.Add(new(targets[i], !failed, output));
        }
        return outcomes;
    }
}

/// <summary>批量操作里单个目标的结果。</summary>
/// <param name="Target">目标(容器 id、镜像引用…)。</param>
/// <param name="Ok">是否成功。</param>
/// <param name="Output">该目标那一段输出。</param>
internal sealed record BatchOutcome(string Target, bool Ok, string Output);
