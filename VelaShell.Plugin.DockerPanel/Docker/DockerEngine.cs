using System.Text;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>远端 docker 的探测结果。</summary>
/// <param name="ClientVersion">docker 客户端版本;探测不到为空。</param>
/// <param name="ServerVersion">docker daemon 版本;连不上 daemon 时为空。</param>
/// <param name="ComposeCommand">可用的 compose 子命令(<c>compose</c> / <c>__standalone__</c>);都没有则为空。</param>
/// <param name="ComposeVersion">compose 版本;没有为空。</param>
/// <param name="Diagnostic">失败时的一行诊断(权限不足、daemon 未启动、没装 docker)。</param>
internal sealed record DockerProbe(
    string ClientVersion,
    string ServerVersion,
    string ComposeCommand,
    string ComposeVersion,
    string Diagnostic)
{
    /// <summary>daemon 可用。</summary>
    public bool IsUsable => ServerVersion.Length > 0;

    /// <summary>compose 可用。</summary>
    public bool HasCompose => ComposeCommand.Length > 0;
}

/// <summary>
/// 远端 docker 引擎:把命令送到某条 SSH 会话上跑。
/// <para>
/// **为什么走 docker CLI 而不是 daemon 的 HTTP API**:HTTP API 要么要求服务器把 daemon
/// 暴露在 TCP 端口上(绝大多数生产机不会,而且那是一个 root 等价的无认证端口),
/// 要么要求把 unix socket 转发出来(SSH 端口转发到的是 TCP,socket 需要额外的中继进程)。
/// 而 <c>docker</c> 命令在每台装了 docker 的机器上都在,权限模型就是用户自己的
/// —— 面板因此**零服务端配置**即可用,也不会给主机开出一个新的攻击面。
/// </para>
/// <para>
/// **为什么每条命令仍套一层 <c>sh -c</c>**:只为了设环境变量(<c>LC_ALL</c> /
/// <c>DOCKER_HOST</c>)。用户的登录 shell 可能是 fish 或 csh,那里 <c>VAR=v cmd</c>
/// 这种前缀赋值不成立;显式起一个 <c>sh</c> 就与登录 shell 无关了。
/// 退出码由 <c>sh</c> 如实透传,标准错误原样保留在自己的流上 ——
/// SDK 1.1 起 <see cref="ExecResult" /> 两样都带,不再需要哨兵与 <c>2&gt;&amp;1</c>。
/// </para>
/// </summary>
internal sealed class DockerEngine(IPluginContext context, string sessionId)
{
    /// <summary>多段命令之间的分隔哨兵(一次 exec 跑多条探测命令时用)。</summary>
    private const string SectionMarker = "__VELA_DOCKER_SECTION__";

    /// <summary>compose v1 独立二进制的内部标记(见 <see cref="ComposePrefix" />)。</summary>
    public const string StandaloneCompose = "__standalone__";

    /// <summary>这条引擎绑定的会话 id。</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>是否给每条 docker 命令加 <c>sudo -n</c> 前缀。</summary>
    public bool UseSudo { get; set; }

    /// <summary>自定义 <c>DOCKER_HOST</c>(留空即用远端默认)。</summary>
    public string DockerHost { get; set; } = string.Empty;

    /// <summary>自定义 <c>DOCKER_CONTEXT</c>(留空即用远端默认)。</summary>
    public string DockerContext { get; set; } = string.Empty;

    /// <summary>最近一次探测的结果。</summary>
    public DockerProbe Probe { get; private set; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>每执行一条命令回调一次(面板的"执行记录"抽屉据此显示远端到底跑了什么)。</summary>
    public Action<string, ExecResult, TimeSpan>? CommandObserved { get; set; }

    /// <summary>docker 命令前缀(含可选的 sudo)。</summary>
    public string DockerPrefix => UseSudo ? "sudo -n docker" : "docker";

    /// <summary>compose 命令前缀;compose 不可用时返回空串。</summary>
    public string ComposePrefix => Probe.ComposeCommand switch
    {
        StandaloneCompose => UseSudo ? "sudo -n docker-compose" : "docker-compose",
        "" => string.Empty,
        var sub => $"{DockerPrefix} {sub}"
    };

    /// <summary>
    /// 远端的 compose 认得 <c>ls</c>。
    /// <para>
    /// 只有 v2 插件形态(<c>docker compose</c>)有 <c>ls</c>;独立的 <c>docker-compose</c>
    /// 是 v1,它根本没有这个子命令。对着 v1 去 <c>ls</c> 只会每次刷新收获一条
    /// "No such command" 并把状态栏刷成一片红 —— 那不是用户能做点什么的信息。
    /// v1 上项目列表就是空的,改用「打开文件…」按路径操作。
    /// </para>
    /// </summary>
    public bool SupportsProjectListing => Probe.ComposeCommand == "compose";

    /// <summary>
    /// 探测远端:一次 exec 同时问客户端版本、daemon 版本与两种 compose 形态。
    /// 拆成四条往返在跨洋链路上要多花大半秒,而这四件事在界面上是**一起**才有意义的。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结果(同时写入 <see cref="Probe" />)。</returns>
    public async Task<DockerProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var docker = DockerPrefix;
        var compose = UseSudo ? "sudo -n docker-compose" : "docker-compose";
        var sections = await RunSectionsAsync(
        [
            $"{docker} version --format '{{{{.Client.Version}}}}'",
            $"{docker} version --format '{{{{.Server.Version}}}}'",
            $"{docker} compose version --short",
            $"{compose} version --short"
        ], TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
        var client = FirstLine(sections.ElementAtOrDefault(0));
        var server = FirstLine(sections.ElementAtOrDefault(1));
        var composeV2 = FirstLine(sections.ElementAtOrDefault(2));
        var composeV1 = FirstLine(sections.ElementAtOrDefault(3));
        // 版本号以数字打头才算数:命令不存在时 shell 回的是 "sh: docker: not found",
        // 权限不够时 docker 回的是 "permission denied while trying to connect ..." ——
        // 两者都会落进同一段输出里,拿来当版本号显示只会更让人糊涂。
        client = LooksLikeVersion(client) ? client : string.Empty;
        server = LooksLikeVersion(server) ? server : string.Empty;
        var composeCmd = string.Empty;
        var composeVer = string.Empty;
        if (LooksLikeVersion(composeV2.TrimStart('v')))
        {
            composeCmd = "compose";
            composeVer = composeV2;
        }
        else if (LooksLikeVersion(composeV1.TrimStart('v')))
        {
            composeCmd = StandaloneCompose;
            composeVer = composeV1;
        }
        var diagnostic = string.Empty;
        if (server.Length == 0)
        {
            var raw = string.Join('\n', sections.Take(2)).Trim();
            diagnostic = DescribeFailure(raw, client.Length > 0);
        }
        Probe = new(client, server, composeCmd, composeVer, diagnostic);
        return Probe;
    }

    /// <summary>执行一条远端命令。</summary>
    /// <param name="script">POSIX sh 脚本(通常就是一条 docker 命令)。</param>
    /// <param name="timeout">超时;为 null 用 30 秒。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。绝不抛异常(超时/会话失效都归一化成失败的结果),
    /// 因为调用点全在界面的命令体里,一个异常等于一次静默的"点了没反应"。</returns>
    public async Task<ExecResult> RunAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var started = Environment.TickCount64;
        ExecResult result;
        try
        {
            result = await context.RemoteExec
                                  .RunAsync(SessionId, Wrap(script), new() { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, cancellationToken)
                                  .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            result = Failure("timed out");
        }
        catch (PluginSessionNotFoundException)
        {
            result = Failure("session is no longer connected");
        }
        catch (Exception ex)
        {
            // 远程执行能力的失败模式(通道被拆、宿主停机)不该把面板打死:
            // 变成一条可见的失败,用户按刷新就能再试。
            result = Failure(ex.Message);
        }
        CommandObserved?.Invoke(script, result, TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
        return result;
    }

    /// <summary>
    /// 流式执行一条长驻命令,逐行回调。
    /// <para>
    /// 用于 <c>docker logs -f</c> 与 <c>docker events</c>。取消令牌触发时宿主给远端进程发
    /// <c>TERM</c>,方法抛 <see cref="OperationCanceledException" /> —— 调用方按预期收尾即可。
    /// </para>
    /// </summary>
    /// <param name="script">命令。</param>
    /// <param name="onLine">逐行回调(**同步**,顺序即到达顺序)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退出码与行数。</returns>
    public async Task<ExecStreamResult> StreamAsync(string script, Action<ExecOutput> onLine, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        try
        {
            var result = await context.RemoteExec.StreamAsync(
                SessionId,
                Wrap(script),
                new() { Timeout = null, IncludeStandardError = true },
                new LineSink(onLine),
                cancellationToken).ConfigureAwait(false);
            CommandObserved?.Invoke(script, new(string.Empty) { ExitCode = result.ExitCode },
                TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CommandObserved?.Invoke(script, Failure(ex.Message), TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
            throw;
        }
    }

    /// <summary>
    /// 一次 exec 跑多条命令,按哨兵切回多段输出。
    /// 探测类命令合并执行是 §9 的纪律:每条命令一次往返,在高延迟链路上是肉眼可见的卡顿。
    /// </summary>
    /// <param name="commands">命令列表(每条各自可以失败,不影响后面的)。</param>
    /// <param name="timeout">整体超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与 <paramref name="commands" /> 等长的输出段;缺失的段为空串。</returns>
    /// <remarks>
    /// 这里(也只有这里)仍然把标准错误并进标准输出:分段是靠在**一条**流里插哨兵实现的,
    /// 两条流各自到达就没法把错误归到正确的段上。分段的用途是探测 —— 那时"这一段说了什么"
    /// 比"它说在哪条流上"重要得多。单条命令走 <see cref="RunAsync" />,两条流是分开的。
    /// </remarks>
    public async Task<IReadOnlyList<string>> RunSectionsAsync(
        IReadOnlyList<string> commands,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder script = new();
        for (var i = 0; i < commands.Count; i++)
        {
            if (i > 0)
            {
                script.Append("printf '%s\\n' ").Append(Sh.Quote(SectionMarker)).Append("; ");
            }
            // 每段用 { ...; } 包住:段内命令失败不该把整条脚本带走。
            script.Append("{ ").Append(commands[i]).Append("; } 2>&1; ");
        }
        var result = await RunAsync(script.ToString(), timeout, cancellationToken).ConfigureAwait(false);
        var parts = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split(SectionMarker, StringSplitOptions.None);
        List<string> sections = [with(commands.Count)];
        for (var i = 0; i < commands.Count; i++)
        {
            sections.Add(i < parts.Length ? parts[i].Trim('\r', '\n') : string.Empty);
        }
        return sections;
    }

    /// <summary>把一段脚本包成远端命令行(设好环境变量;单测直接验这一层)。</summary>
    /// <param name="script">脚本正文。</param>
    /// <returns>交给远程执行能力的命令行。</returns>
    public string Wrap(string script)
    {
        StringBuilder inner = new();
        // LC_ALL=C:docker CLI 的部分错误信息与 `docker system df` 的表头会跟随远端语言环境,
        // 解析与展示都按英文来,免得同一台机器换个 locale 就解析不出东西。
        inner.Append("export LC_ALL=C LANG=C; ");
        if (DockerHost.Length > 0)
        {
            inner.Append("export DOCKER_HOST=").Append(Sh.Quote(DockerHost)).Append("; ");
        }
        if (DockerContext.Length > 0)
        {
            inner.Append("export DOCKER_CONTEXT=").Append(Sh.Quote(DockerContext)).Append("; ");
        }
        inner.Append(script);
        return $"sh -c {Sh.Quote(inner.ToString())}";
    }

    /// <summary>把一条本地失败(超时、通道断)做成一个"退出码 -1"的结果。</summary>
    /// <param name="message">失败说明。</param>
    /// <returns>失败结果。</returns>
    private static ExecResult Failure(string message) => new(string.Empty) { Error = message, ExitCode = -1 };

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        return string.Empty;
    }

    private static bool LooksLikeVersion(string text) => text.Length > 0 && char.IsAsciiDigit(text[0]);

    /// <summary>
    /// 把 docker 的失败输出翻成一句能照着做的话。
    /// 三种失败在服务器上极其常见,而原始文案要么太长要么太技术:
    /// 没装、daemon 没起、当前用户不在 docker 组。第三种是**唯一**面板能替用户解决的
    /// (勾上 sudo),所以必须认出来单说。
    /// </summary>
    /// <param name="raw">docker 的原始输出。</param>
    /// <param name="hasClient">客户端是否存在。</param>
    /// <returns>一行诊断。</returns>
    public static string DescribeFailure(string raw, bool hasClient)
    {
        if (raw.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "denied";
        }
        if (raw.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("no tty present", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("sudo: a password is required", StringComparison.OrdinalIgnoreCase))
        {
            return "sudo-password";
        }
        if (!hasClient && (raw.Contains("not found", StringComparison.OrdinalIgnoreCase)
                           || raw.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                           || raw.Length == 0))
        {
            return "missing";
        }
        if (raw.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase))
        {
            return "daemon";
        }
        return raw.Length > 0 ? "other" : "missing";
    }

    /// <summary>
    /// 同步转发的输出接收器。
    /// <para>
    /// **刻意不是 <see cref="Progress{T}" />**:它会把每次回调 Post 到线程池,
    /// 顺序当场就没了 —— 而日志流的顺序就是它全部的意义。宿主是在读行的线程上
    /// 顺序调 <see cref="IProgress{T}.Report" /> 的,同步转发即保序。
    /// </para>
    /// </summary>
    /// <param name="onLine">逐行回调。</param>
    private sealed class LineSink(Action<ExecOutput> onLine) : IProgress<ExecOutput>
    {
        /// <inheritdoc />
        public void Report(ExecOutput value) => onLine(value);
    }
}
