namespace VelaShell.Plugin.DockerPanel.Docker;

internal sealed partial class DockerApi
{
    /// <summary>
    /// 列出 compose 项目。
    /// <para>
    /// <c>docker compose ls</c> 只认**当前正在跑**(或 <c>--all</c> 下曾经起过)的项目 ——
    /// 它是从容器标签反推出来的,而不是去扫盘找 yml。所以一个从没 <c>up</c> 过的项目
    /// 在这里永远不出现;那种情况由面板的"按文件操作"补上(用户给一个 compose 文件路径)。
    /// </para>
    /// </summary>
    /// <param name="all">连已停的项目一起列。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>项目列表与原始结果。</returns>
    public async Task<(IReadOnlyList<ComposeProjectItem> Items, DockerResult Result)> ListComposeProjectsAsync(
        bool all, CancellationToken cancellationToken)
    {
        string compose = Engine.ComposePrefix;
        if (compose.Length == 0)
        {
            return ([], new(-1, "compose is not available on this host"));
        }
        if (!Engine.SupportsProjectListing)
        {
            // v1 的 docker-compose 没有 `ls`。当作"列不出来"而不是"失败":
            // 这条路上用户该走的是「打开文件…」,而不是盯着一条每 5 秒重印一次的错误。
            return ([], new(0, string.Empty));
        }
        DockerResult result = await Engine
                                    .RunAsync($"{compose} ls{(all ? " --all" : "")} --format json", null, cancellationToken)
                                    .ConfigureAwait(false);
        List<ComposeProjectItem> items = [];
        foreach (IReadOnlyDictionary<string, string> row in DockerJson.ParseArray(result.Output))
        {
            string name = DockerJson.Str(row, "Name");
            if (name.Length == 0)
            {
                continue;
            }
            items.Add(new()
            {
                Name = name,
                Status = DockerJson.Str(row, "Status"),
                ConfigFiles = DockerJson.Str(row, "ConfigFiles")
            });
        }
        return ([.. items.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)], result);
    }

    /// <summary>
    /// 拼一条 compose 命令。
    /// <para>
    /// 同时给 <c>-p</c> 与 <c>-f</c>:光给项目名,compose 找不到 yml(它不记得项目从哪来);
    /// 光给文件,项目名会按目录名重新推导,于是 <c>down</c> 掉的可能是另一个项目。
    /// 两个都给才与用户当初 <c>up</c> 时的身份对得上。
    /// </para>
    /// </summary>
    /// <param name="project">项目名;为空则只按文件。</param>
    /// <param name="configFile">compose 文件路径;为空则只按项目名(要求当前目录里有 yml,基本只在 ls 之后立即用)。</param>
    /// <param name="arguments">子命令与参数(已自行引用好)。</param>
    /// <returns>完整命令行;compose 不可用时返回空串。</returns>
    public string BuildComposeCommand(string project, string configFile, string arguments)
    {
        string compose = Engine.ComposePrefix;
        if (compose.Length == 0)
        {
            return string.Empty;
        }
        string command = compose;
        if (project.Length > 0)
        {
            command += $" -p {Sh.Quote(project)}";
        }
        if (configFile.Length > 0)
        {
            command += $" -f {Sh.Quote(configFile)}";
            // --project-directory:compose 用它解析 yml 里的相对路径(bind mount、env_file)。
            // 不给的话 v2 会以**当前工作目录**为基准,而 exec 通道的当前目录是登录目录,
            // 于是 ./data 会解析到 ~/data —— 一个安静地挂错盘的 bug。
            string directory = ParentDirectory(configFile);
            if (directory.Length > 0)
            {
                command += $" --project-directory {Sh.Quote(directory)}";
            }
        }
        return $"{command} {arguments}";
    }

    /// <summary>对一个 compose 项目执行动作。</summary>
    /// <param name="project">项目名。</param>
    /// <param name="configFile">compose 文件路径。</param>
    /// <param name="arguments">子命令与参数(如 <c>up -d</c>、<c>down -v</c>)。</param>
    /// <param name="timeout">超时;为空用长超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public async Task<DockerResult> ComposeAsync(
        string project, string configFile, string arguments, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        string command = BuildComposeCommand(project, configFile, arguments);
        if (command.Length == 0)
        {
            return new(-1, "compose is not available on this host");
        }
        DockerResult result = await Engine.RunAsync(command, timeout ?? LongTimeout, cancellationToken).ConfigureAwait(false);
        return result with { Output = OutputText.Collapse(result.Output) };
    }

    /// <summary>项目内服务的状态表。</summary>
    /// <param name="project">项目名。</param>
    /// <param name="configFile">compose 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表格文本。</returns>
    public async Task<string> ComposePsAsync(string project, string configFile, CancellationToken cancellationToken)
    {
        DockerResult result = await ComposeAsync(project, configFile, "ps -a", TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>项目的合并日志。</summary>
    /// <param name="project">项目名。</param>
    /// <param name="configFile">compose 文件路径。</param>
    /// <param name="tail">每个服务取多少行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>日志文本。</returns>
    public async Task<string> ComposeLogsAsync(string project, string configFile, int tail, CancellationToken cancellationToken)
    {
        DockerResult result = await ComposeAsync(project, configFile, $"logs --no-color --tail {tail}", TimeSpan.FromSeconds(90), cancellationToken)
                                    .ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>把 compose 文件解析成最终配置(校验 + 展开变量);语法有错时输出就是错误说明。</summary>
    /// <param name="project">项目名。</param>
    /// <param name="configFile">compose 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>展开后的 YAML 或错误文本。</returns>
    public async Task<string> ComposeConfigAsync(string project, string configFile, CancellationToken cancellationToken)
    {
        DockerResult result = await ComposeAsync(project, configFile, "config", TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>取一个路径的父目录(远端是 POSIX 路径,不能用 <c>Path.GetDirectoryName</c> —— 它在 Windows 上会把 <c>/</c> 换成 <c>\</c>)。</summary>
    /// <param name="path">远端路径。</param>
    /// <returns>父目录;没有分隔符时返回空串。</returns>
    internal static string ParentDirectory(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash switch
        {
            < 0 => string.Empty,
            0 => "/",
            _ => path[..slash]
        };
    }
}
