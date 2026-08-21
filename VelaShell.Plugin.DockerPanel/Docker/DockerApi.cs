using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 面板用得到的全部 docker 操作,按域拆成几个 partial 文件
/// (<c>.Containers</c> / <c>.Images</c> / <c>.VolumesNetworks</c> / <c>.Compose</c> / <c>.System</c>)。
/// <para>
/// 这一层只做两件事:**拼命令**与**解析输出**。它不认识界面,也不持有状态 ——
/// 所以命令拼装可以在单测里逐条比对(见 <c>DockerApiTests</c>),
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
    /// 对一批目标执行同一条命令,并把每个目标的成败分别记下来。
    /// <para>
    /// docker 的批量子命令(<c>stop a b c</c>)本身就接受多个目标,而且行为正好够用:
    /// **成功的目标原样回显到标准输出**(一行一个),失败的写到标准错误。
    /// 于是一次往返就能分辨"停了八个、两个失败",而不是把整批说成"失败" ——
    /// 后者最要命的地方在于它是**假的**:那八个确实已经停了。
    /// </para>
    /// <para>
    /// (SDK 1.1 之前拿不到标准错误与退出码,这里只能一条条跑、再按输出文字猜成败。
    /// 现在不用猜了,往返次数也从 N 次降回 1 次。)
    /// </para>
    /// </summary>
    /// <param name="targets">目标(容器 id、镜像引用、卷名…)。</param>
    /// <param name="build">按目标列表生成一条命令。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个目标的结果。</returns>
    public async Task<IReadOnlyList<BatchOutcome>> RunBatchAsync(
        IReadOnlyList<string> targets,
        Func<IReadOnlyList<string>, string> build,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return [];
        }
        var result = await Engine.RunAsync(build(targets), timeout, cancellationToken).ConfigureAwait(false);
        HashSet<string> echoed = [with(StringComparer.Ordinal)];
        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                echoed.Add(trimmed);
            }
        }
        string[] errorLines = [.. result.Error.Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0)];
        List<BatchOutcome> outcomes = [with(targets.Count)];
        foreach (var target in targets)
        {
            // 回显里出现过就是成功。整条命令成功(退出码 0)但**一个都没回显**时也算成功 ——
            // 个别子命令(`network disconnect`、`update`)本来就不回显目标名。
            var ok = echoed.Contains(target) || (result.IsSuccess && echoed.Count == 0);
            outcomes.Add(new(target, ok, ok ? string.Empty : FindReason(errorLines, target, result)));
        }
        return outcomes;
    }

    /// <summary>从标准错误里挑出与某个目标有关的那一行;挑不出就用整体失败说明。</summary>
    /// <param name="errorLines">标准错误的各行。</param>
    /// <param name="target">目标。</param>
    /// <param name="result">整条命令的结果。</param>
    /// <returns>一行原因。</returns>
    private static string FindReason(IReadOnlyList<string> errorLines, string target, ExecResult result)
    {
        // docker 的错误行里一般带着出问题的那个名字/id,而且用的是**用户传进去的那个写法**。
        // 短 id 也试一次:有些子命令会把长 id 截短了再回报。
        var shortTarget = target.Length > 12 ? target[..12] : target;
        foreach (var line in errorLines)
        {
            if (line.Contains(target, StringComparison.OrdinalIgnoreCase)
                || line.Contains(shortTarget, StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return errorLines.Count > 0 ? errorLines[0] : result.FailureText;
    }
}

/// <summary>批量操作里单个目标的结果。</summary>
/// <param name="Target">目标(容器 id、镜像引用…)。</param>
/// <param name="IsSuccess">是否成功。</param>
/// <param name="Output">失败原因(成功时为空)。</param>
internal sealed record BatchOutcome(string Target, bool IsSuccess, string Output);
