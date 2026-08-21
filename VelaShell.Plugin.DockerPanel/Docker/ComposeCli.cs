using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>POSIX shell 引用。</summary>
public static class Sh
{
    /// <summary>单引号引用一个参数(内部单引号转成 <c>'\''</c>)。</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

/// <summary>一个 compose 项目。</summary>
/// <param name="Name">项目名。</param>
/// <param name="Status">daemon 报的状态串,如 <c>running(3)</c>。</param>
/// <param name="ConfigFiles">compose 文件路径(逗号分隔的原文)。</param>
public sealed record ComposeProject(string Name, string Status, string ConfigFiles)
{
    /// <summary>第一个 compose 文件的路径。</summary>
    public string PrimaryFile => ConfigFiles.Split(',', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } files
        ? files[0].Trim()
        : "";

    /// <summary>项目目录(compose 文件所在目录)。</summary>
    public string ProjectDirectory
    {
        get
        {
            string file = PrimaryFile;
            int slash = file.LastIndexOf('/');
            return slash > 0 ? file[..slash] : "";
        }
    }

    /// <summary>运行中的服务数(从状态串里解出来;解不出给 0)。</summary>
    public int RunningCount => ParseCount("running");

    /// <summary>已退出的服务数。</summary>
    public int ExitedCount => ParseCount("exited");

    private int ParseCount(string keyword)
    {
        int at = Status.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return 0;
        }
        int open = Status.IndexOf('(', at);
        int close = open > 0 ? Status.IndexOf(')', open) : -1;
        return close > open && int.TryParse(Status.AsSpan(open + 1, close - open - 1), out int n) ? n : 0;
    }
}

/// <summary>compose 项目里的一个服务。</summary>
public sealed record ComposeService
{
    /// <summary>服务名。</summary>
    [JsonPropertyName("Service")]
    public string Service { get; init; } = "";

    /// <summary>容器名。</summary>
    [JsonPropertyName("Name")]
    public string Name { get; init; } = "";

    /// <summary>镜像。</summary>
    [JsonPropertyName("Image")]
    public string? Image { get; init; }

    /// <summary>状态,如 <c>running</c>。</summary>
    [JsonPropertyName("State")]
    public string? State { get; init; }

    /// <summary>人话状态。</summary>
    [JsonPropertyName("Status")]
    public string? Status { get; init; }

    /// <summary>端口摘要。</summary>
    [JsonPropertyName("Publishers")]
    public ComposePublisher[]? Publishers { get; init; }

    /// <summary>端口摘要文本。</summary>
    public string PortsText
    {
        get
        {
            if (Publishers is null or { Length: 0 })
            {
                return "—";
            }
            IEnumerable<string> parts = Publishers
                .Where(p => p.PublishedPort > 0)
                .Select(p => $"{p.PublishedPort}→{p.TargetPort}")
                .Distinct();
            string text = string.Join(", ", parts);
            return text.Length == 0 ? "—" : text;
        }
    }
}

/// <summary>compose ps 里的一条端口发布。</summary>
public sealed record ComposePublisher
{
    /// <summary>容器内端口。</summary>
    public int TargetPort { get; init; }

    /// <summary>宿主端口。</summary>
    public int PublishedPort { get; init; }

    /// <summary>协议。</summary>
    public string? Protocol { get; init; }
}

/// <summary>
/// Compose 走 CLI,不走 HTTP API。
/// <para>
/// <b>这不是偷懒。</b> Docker Engine API 里**没有 compose 这一块** —— compose 是一个
/// CLI 插件,它读 yml、算出依赖顺序,再调用一串普通的 Engine API。想在面板里"用 API 做
/// compose",等于把 compose 自己重写一遍,而且注定与远端装的那个版本行为不一致。
/// 所以项目管理照旧走 <c>docker compose</c>,经 SDK 的远程执行下发。
/// </para>
/// <para>
/// 代价写在界面上:<b>本机端点没有 compose 页</b>(那需要在宿主机上跑进程,不在插件的
/// 职责里),而远端端点上 compose 的版本、行为与用户在终端里敲的完全一致。
/// </para>
/// </summary>
public sealed class ComposeCli(IRemoteExecApi exec, IRemoteFsApi remoteFs, string sessionId)
{
    /// <summary>
    /// 列出 compose 项目。
    /// <para>
    /// <c>compose ls</c> 只认得**起过至少一次**的项目 —— 它读的是容器上的标签,而不是
    /// 磁盘上的 yml。所以列表为空不代表机器上没有 compose 文件;界面另给一个
    /// 「按路径打开」的入口。
    /// </para>
    /// </summary>
    public async Task<ComposeProject[]> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        ExecResult result = await exec.RunAsync(sessionId, "docker compose ls --all --format json",
            new ExecOptions { Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // compose v1(独立的 docker-compose)根本没有 ls 子命令。这时给空列表 +
            // 上层的提示,而不是把一条看不懂的错误摔在用户脸上。
            return [];
        }
        ComposeListEntry[]? entries = DockerJson.TryDeserialize<ComposeListEntry[]>(result.Output.Trim());
        return entries is null
            ? []
            : [.. entries.Select(e => new ComposeProject(e.Name ?? "", e.Status ?? "", e.ConfigFiles ?? ""))];
    }

    /// <summary>列出项目里的服务。</summary>
    public async Task<ComposeService[]> ListServicesAsync(ComposeProject project, CancellationToken cancellationToken = default)
    {
        ExecResult result = await exec.RunAsync(sessionId, $"{Prefix(project)} ps -a --format json",
            new ExecOptions { Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return [];
        }
        string text = result.Output.Trim();
        if (text.Length == 0)
        {
            return [];
        }
        // compose 的 --format json 在不同版本里一会儿给 JSON 数组、一会儿给 NDJSON。
        // 两种都收,免得面板在某个次版本上突然空白。
        if (text.StartsWith('['))
        {
            return DockerJson.TryDeserialize<ComposeService[]>(text) ?? [];
        }
        List<ComposeService> services = [];
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (DockerJson.TryDeserialize<ComposeService>(line.Trim()) is { } service)
            {
                services.Add(service);
            }
        }
        return [.. services];
    }

    /// <summary>展开后的配置(顺带就是一次语法校验)。</summary>
    public async Task<ExecResult> ConfigAsync(ComposeProject project, CancellationToken cancellationToken = default) =>
        await exec.RunAsync(sessionId, $"{Prefix(project)} config",
            new ExecOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 跑一条 compose 子命令,输出**边跑边回调**(<c>up -d</c> 要几十秒,
    /// 用户得看见它在动)。
    /// </summary>
    /// <param name="project">目标项目。</param>
    /// <param name="arguments">子命令与参数,如 <c>up -d</c>。</param>
    /// <param name="onOutput">逐行输出。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退出码。</returns>
    public async Task<int> RunAsync(ComposeProject project, string arguments,
        IProgress<ExecOutput> onOutput, CancellationToken cancellationToken = default)
    {
        ExecStreamResult result = await exec.StreamAsync(sessionId, $"{Prefix(project)} {arguments}",
            new ExecStreamOptions { IncludeStandardError = true }, onOutput, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>
    /// 跟着一个项目的**合并日志**(<c>compose logs -f</c>)。
    /// <para>
    /// 与容器页那个合并流不是一回事:那一个是面板自己把 N 条 <c>docker logs</c> 并起来,
    /// 这一个交给 compose 自己去并 —— 它认得项目里有哪些服务,包括面板列表还没刷到的新容器。
    /// </para>
    /// </summary>
    public Task<int> FollowLogsAsync(ComposeProject project, string tail, IProgress<ExecOutput> onOutput,
        CancellationToken cancellationToken = default) =>
        RunAsync(project, $"logs -f --no-color --tail {Sh.Quote(tail)}", onOutput, cancellationToken);

    /// <summary>对单个服务跑一条子命令(逐服务的日志 / 重启走这里)。</summary>
    public Task<int> RunForServiceAsync(ComposeProject project, string arguments, string service,
        IProgress<ExecOutput> onOutput, CancellationToken cancellationToken = default) =>
        RunAsync(project, $"{arguments} {Sh.Quote(service)}", onOutput, cancellationToken);

    /// <summary>
    /// 项目的 <c>.env</c> 路径。
    /// <para>
    /// compose 只认项目目录下那一个 <c>.env</c>(<c>--env-file</c> 另说),
    /// 所以这里不去猜别的位置 —— 猜错了改的是一个没人读的文件。
    /// </para>
    /// </summary>
    public static string EnvPath(ComposeProject project) =>
        project.ProjectDirectory is { Length: > 0 } dir ? $"{dir}/.env" : "";

    /// <summary>读远端的 compose 文件(经 SFTP,不经 shell)。</summary>
    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await remoteFs.ReadAllBytesAsync(sessionId, path, 4 << 20, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 写回远端的 compose 文件(经 SFTP)。
    /// <b>覆盖写</b>,界面上必须走"手打确认串"那一档闸门。
    /// </summary>
    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
        remoteFs.WriteAllBytesAsync(sessionId, path, Encoding.UTF8.GetBytes(content), cancellationToken);

    /// <summary>探测远端有没有 <c>docker compose</c>(v2)。</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        ExecResult result = await exec.RunAsync(sessionId, "docker compose version --short",
            new ExecOptions { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess;
    }

    /// <summary>
    /// compose 命令的固定前缀。
    /// <para>
    /// <c>-p</c>、<c>-f</c> 与 <c>--project-directory</c> 三个一起钉住,少一个都出错:
    /// 光给项目名,compose 找不到 yml(它不记得项目从哪来);光给文件,项目名会按目录名
    /// 重新推导,<c>down</c> 掉的可能是另一个项目;不给 project-directory,yml 里的
    /// <c>./data</c> 会以**登录目录**为基准解析 —— 一个安静地挂错盘的 bug。
    /// </para>
    /// </summary>
    private static string Prefix(ComposeProject project)
    {
        var sb = new StringBuilder("docker compose -p ").Append(Sh.Quote(project.Name));
        foreach (string file in project.ConfigFiles.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(" -f ").Append(Sh.Quote(file.Trim()));
        }
        if (project.ProjectDirectory is { Length: > 0 } directory)
        {
            sb.Append(" --project-directory ").Append(Sh.Quote(directory));
        }
        return sb.ToString();
    }
}

/// <summary><c>docker compose ls --format json</c> 的一项。</summary>
internal sealed record ComposeListEntry
{
    [JsonPropertyName("Name")]
    public string? Name { get; init; }

    [JsonPropertyName("Status")]
    public string? Status { get; init; }

    [JsonPropertyName("ConfigFiles")]
    public string? ConfigFiles { get; init; }
}
