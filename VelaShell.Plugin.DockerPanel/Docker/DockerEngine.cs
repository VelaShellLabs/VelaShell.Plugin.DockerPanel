using System.Text;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>一条远端命令的结果。</summary>
/// <param name="ExitCode">退出码;<c>-1</c> 表示没能拿到退出码(超时、通道异常、哨兵丢失)。</param>
/// <param name="Output">合并后的标准输出与标准错误(已去掉哨兵行)。</param>
internal sealed record DockerResult(int ExitCode, string Output)
{
    /// <summary>命令是否成功。</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>失败时给人看的一行原因(取输出里第一行非空文本;没有就退回退出码)。</summary>
    public string FailureText
    {
        get
        {
            foreach (string line in Output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return ExitCode < 0 ? "no exit status" : $"exit {ExitCode}";
        }
    }
}

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
/// 远端 docker 引擎:把一段脚本送到某条 SSH 会话上跑,并把退出码带回来。
/// <para>
/// **为什么走 docker CLI 而不是 daemon 的 HTTP API**:HTTP API 要么要求服务器把 daemon
/// 暴露在 TCP 端口上(绝大多数生产机不会,而且那是一个 root 等价的无认证端口),
/// 要么要求把 unix socket 转发出来(SSH 端口转发到的是 TCP,socket 需要额外的中继进程)。
/// 而 <c>docker</c> 命令在每台装了 docker 的机器上都在,权限模型就是用户自己的
/// —— 面板因此**零服务端配置**即可用,也不会给主机开出一个新的攻击面。
/// </para>
/// <para>
/// **为什么每条命令都套一层 <c>sh -c</c>**:宿主的 <see cref="IRemoteExecApi" /> 只回标准输出
/// (没有标准错误、没有退出码),而 docker 把绝大多数错误写在标准错误上 ——
/// 不合并就会出现"删除失败了但界面一片安静"。套一层之后,
/// <c>2&gt;&amp;1</c> 把两条流并起来,末尾的哨兵把 <c>$?</c> 带回来,
/// 用户登录 shell 是 bash 还是 fish 也不再有影响(fish 里没有 <c>$?</c>)。
/// </para>
/// </summary>
internal sealed class DockerEngine(IPluginContext context, string sessionId)
{
    /// <summary>退出码哨兵。取输出里**最后**一次出现的那个,容器日志里恰好印出同样的串也不会误判。</summary>
    private const string ExitMarker = "__VELA_DOCKER_RC:";

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
    public Action<string, DockerResult, TimeSpan>? CommandObserved { get; set; }

    /// <summary>docker 命令前缀(含可选的 sudo)。</summary>
    public string DockerPrefix => UseSudo ? "sudo -n docker" : "docker";

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

    /// <summary>compose 命令前缀;compose 不可用时返回空串。</summary>
    public string ComposePrefix => Probe.ComposeCommand switch
    {
        StandaloneCompose => UseSudo ? "sudo -n docker-compose" : "docker-compose",
        "" => string.Empty,
        var sub => $"{DockerPrefix} {sub}"
    };

    /// <summary>
    /// 探测远端:一次 exec 同时问客户端版本、daemon 版本与两种 compose 形态。
    /// 拆成四条往返在跨洋链路上要多花大半秒,而这四件事在界面上是**一起**才有意义的。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结果(同时写入 <see cref="Probe" />)。</returns>
    public async Task<DockerProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        string docker = DockerPrefix;
        string compose = UseSudo ? "sudo -n docker-compose" : "docker-compose";
        IReadOnlyList<string> sections = await RunSectionsAsync(
        [
            $"{docker} version --format '{{{{.Client.Version}}}}'",
            $"{docker} version --format '{{{{.Server.Version}}}}'",
            $"{docker} compose version --short",
            $"{compose} version --short"
        ], TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
        string client = FirstLine(sections.ElementAtOrDefault(0));
        string server = FirstLine(sections.ElementAtOrDefault(1));
        string composeV2 = FirstLine(sections.ElementAtOrDefault(2));
        string composeV1 = FirstLine(sections.ElementAtOrDefault(3));
        // 版本号以数字打头才算数:命令不存在时 shell 回的是 "sh: docker: not found",
        // 权限不够时 docker 回的是 "permission denied while trying to connect ..." ——
        // 两者都会落进同一段输出里,拿来当版本号显示只会更让人糊涂。
        client = LooksLikeVersion(client) ? client : string.Empty;
        server = LooksLikeVersion(server) ? server : string.Empty;
        string composeCmd = string.Empty;
        string composeVer = string.Empty;
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
        string diagnostic = string.Empty;
        if (server.Length == 0)
        {
            string raw = string.Join('\n', sections.Take(2)).Trim();
            diagnostic = DescribeFailure(raw, client.Length > 0);
        }
        Probe = new(client, server, composeCmd, composeVer, diagnostic);
        return Probe;
    }

    /// <summary>执行一段远端脚本并取回退出码与合并输出。</summary>
    /// <param name="script">POSIX sh 脚本(多条命令用 <c>;</c> 或换行分隔)。</param>
    /// <param name="timeout">超时;为 null 用 30 秒。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。绝不抛异常(超时/会话失效都归一化成失败的结果),
    /// 因为调用点全在界面的命令体里,一个异常等于一次静默的"点了没反应"。</returns>
    public async Task<DockerResult> RunAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        long started = Environment.TickCount64;
        DockerResult result;
        try
        {
            ExecResult raw = await context.RemoteExec
                                         .RunAsync(SessionId, Wrap(script), new() { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, cancellationToken)
                                         .ConfigureAwait(false);
            result = Split(raw.Output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            result = new(-1, "timed out");
        }
        catch (PluginSessionNotFoundException)
        {
            result = new(-1, "session is no longer connected");
        }
        catch (Exception ex)
        {
            // 远程执行能力的失败模式(通道被拆、宿主停机)不该把面板打死:
            // 变成一条可见的失败,用户按刷新就能再试。
            result = new(-1, ex.Message);
        }
        CommandObserved?.Invoke(script, result, TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
        return result;
    }

    /// <summary>
    /// 一次 exec 跑多条命令,按哨兵切回多段输出。
    /// 探测类命令合并执行是 §9 的纪律:每条命令一次往返,在高延迟链路上是肉眼可见的卡顿。
    /// </summary>
    /// <param name="commands">命令列表(每条各自可以失败,不影响后面的)。</param>
    /// <param name="timeout">整体超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与 <paramref name="commands" /> 等长的输出段;缺失的段为空串。</returns>
    public async Task<IReadOnlyList<string>> RunSectionsAsync(
        IReadOnlyList<string> commands,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder script = new();
        for (int i = 0; i < commands.Count; i++)
        {
            if (i > 0)
            {
                script.Append("printf '%s\\n' ").Append(Sh.Quote(SectionMarker)).Append("; ");
            }
            // 每段用 { ...; } 包住:段内命令失败不该把整条脚本带走(sh 默认就不会,
            // 但用户如果在自定义 DOCKER_HOST 里塞了 set -e 之类,包一层更稳)。
            script.Append("{ ").Append(commands[i]).Append("; } 2>&1; ");
        }
        DockerResult result = await RunAsync(script.ToString(), timeout, cancellationToken).ConfigureAwait(false);
        string[] parts = result.Output.Split(SectionMarker, StringSplitOptions.None);
        List<string> sections = new(commands.Count);
        for (int i = 0; i < commands.Count; i++)
        {
            sections.Add(i < parts.Length ? parts[i].Trim('\r', '\n') : string.Empty);
        }
        return sections;
    }

    /// <summary>把一段脚本包成可以带回退出码的远端命令行(单测直接验这一层)。</summary>
    /// <param name="script">脚本正文。</param>
    /// <returns>交给 <see cref="IRemoteExecApi" /> 的命令行。</returns>
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
        // 收尾用换行而不是分号:POSIX 只要求 `}` 前有一个命令终止符,而脚本本身可能已经
        // 以 `;` 结束(RunSectionsAsync 就是),再补一个分号就成了 `; ;` —— 那是语法错误。
        inner.Append("{ ").Append(script).Append("\n} 2>&1; ");
        inner.Append("printf '\\n").Append(ExitMarker).Append("%d__\\n' \"$?\"");
        return $"sh -c {Sh.Quote(inner.ToString())}";
    }

    /// <summary>从带哨兵的输出里切出退出码与正文(单测直接验这一层)。</summary>
    /// <param name="raw">远端回来的原始输出。</param>
    /// <returns>解析后的结果;哨兵缺失时退出码为 -1、正文原样保留。</returns>
    public static DockerResult Split(string raw)
    {
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        int marker = raw.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return new(-1, raw.Trim('\n'));
        }
        int digits = marker + ExitMarker.Length;
        int end = digits;
        while (end < raw.Length && (char.IsAsciiDigit(raw[end]) || (end == digits && raw[end] == '-')))
        {
            end++;
        }
        int code = int.TryParse(raw.AsSpan(digits, end - digits), out int parsed) ? parsed : -1;
        return new(code, raw[..marker].Trim('\n'));
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
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
}
