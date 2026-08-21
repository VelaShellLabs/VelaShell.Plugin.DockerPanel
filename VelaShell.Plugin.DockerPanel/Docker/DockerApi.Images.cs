using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>"用镜像跑一个容器"的表单值。</summary>
internal sealed record RunSpec
{
    /// <summary>镜像引用。</summary>
    public required string Image { get; init; }

    /// <summary>容器名;为空让 docker 随机起。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>端口映射,一行一条(<c>8080:80</c> 或 <c>127.0.0.1:8080:80/tcp</c>)。</summary>
    public string Ports { get; init; } = string.Empty;

    /// <summary>卷挂载,一行一条(<c>/data:/var/lib/data:ro</c> 或 <c>myvol:/data</c>)。</summary>
    public string Volumes { get; init; } = string.Empty;

    /// <summary>环境变量,一行一条(<c>KEY=value</c>)。</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>加入的网络;为空用默认 bridge。</summary>
    public string Network { get; init; } = string.Empty;

    /// <summary>重启策略。</summary>
    public string RestartPolicy { get; init; } = "unless-stopped";

    /// <summary>后台运行(<c>-d</c>)。绝大多数情况都该开着 —— 面板没有交互式终端。</summary>
    public bool Detach { get; init; } = true;

    /// <summary>退出即删(<c>--rm</c>)。</summary>
    public bool RemoveOnExit { get; init; }

    /// <summary>额外参数,原样接进命令行(与在终端里手敲同权)。</summary>
    public string ExtraArgs { get; init; } = string.Empty;

    /// <summary>覆盖镜像的启动命令;为空用镜像默认。</summary>
    public string Command { get; init; } = string.Empty;
}

internal sealed partial class DockerApi
{
    /// <summary>列出镜像。</summary>
    /// <param name="all">连中间层一起列(<c>-a</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>镜像列表与原始结果。</returns>
    public async Task<(IReadOnlyList<ImageItem> Items, ExecResult Result)> ListImagesAsync(
        bool all, CancellationToken cancellationToken)
    {
        var command = $"{D} images{(all ? " -a" : "")} --no-trunc --format '{{{{json .}}}}'";
        var result = await Engine.RunAsync(command, null, cancellationToken).ConfigureAwait(false);
        List<ImageItem> items = [];
        foreach (var row in DockerJson.ParseLines(result.Output))
        {
            items.Add(new()
            {
                Id = DockerJson.Str(row, "ID"),
                Repository = DockerJson.Str(row, "Repository"),
                Tag = DockerJson.Str(row, "Tag"),
                Digest = DockerJson.Str(row, "Digest"),
                CreatedAt = DockerJson.Str(row, "CreatedAt"),
                CreatedSince = DockerJson.Str(row, "CreatedSince"),
                Size = DockerJson.Str(row, "Size")
            });
        }
        return (items, result);
    }

    /// <summary>拉镜像。</summary>
    /// <param name="reference">镜像引用(可带 tag 或 digest)。</param>
    /// <param name="allTags">拉全部标签(<c>-a</c>)。</param>
    /// <param name="platform">指定平台(如 <c>linux/arm64</c>);为空跟随远端默认。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果(输出已折叠掉进度条重画)。</returns>
    public async Task<ExecResult> PullImageAsync(
        string reference, bool allTags, string platform, CancellationToken cancellationToken)
    {
        var command = $"{D} pull";
        if (allTags)
        {
            command += " -a";
        }
        if (platform.Length > 0)
        {
            command += $" --platform {Sh.Quote(platform)}";
        }
        command += $" {Sh.Quote(reference)}";
        var result = await Engine.RunAsync(command, LongTimeout, cancellationToken).ConfigureAwait(false);
        return result with { Output = OutputText.Collapse(result.Output) };
    }

    /// <summary>删镜像。</summary>
    /// <param name="references">镜像引用或 id。</param>
    /// <param name="force">被容器引用也删(<c>-f</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个镜像的结果。</returns>
    public Task<IReadOnlyList<BatchOutcome>> RemoveImagesAsync(
        IReadOnlyList<string> references, bool force, CancellationToken cancellationToken) =>
        RunBatchAsync(references, all => $"{D} rmi{(force ? " -f" : "")} {Sh.QuoteAll(all)}", LifecycleTimeout, cancellationToken);

    /// <summary>给镜像打标签。</summary>
    /// <param name="source">源引用或 id。</param>
    /// <param name="target">目标引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<ExecResult> TagImageAsync(string source, string target, CancellationToken cancellationToken) =>
        Engine.RunAsync($"{D} tag {Sh.Quote(source)} {Sh.Quote(target)}", null, cancellationToken);

    /// <summary>推镜像到仓库。</summary>
    /// <param name="reference">镜像引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public async Task<ExecResult> PushImageAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync($"{D} push {Sh.Quote(reference)}", LongTimeout, cancellationToken).ConfigureAwait(false);
        return result with { Output = OutputText.Collapse(result.Output) };
    }

    /// <summary>镜像的构建历史。</summary>
    /// <param name="reference">镜像引用或 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表格文本。</returns>
    public async Task<string> ImageHistoryAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await Engine
                                    .RunAsync($"{D} history --no-trunc --format 'table {{{{.ID}}}}\\t{{{{.CreatedSince}}}}\\t{{{{.Size}}}}\\t{{{{.CreatedBy}}}}' {Sh.Quote(reference)}",
                                        null, cancellationToken)
                                    .ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>镜像详情。</summary>
    /// <param name="reference">镜像引用或 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的 JSON,或错误文本。</returns>
    public async Task<string> InspectImageAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync($"{D} image inspect {Sh.Quote(reference)}", null, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? DockerJson.Pretty(result.Output) : result.Output;
    }

    /// <summary>
    /// 拼出 <c>docker run</c>。
    /// <para>
    /// 拼装单独抽出来是为了两件事:一是单测能逐条比对(端口/卷/环境变量的引用最容易写错),
    /// 二是界面能在按下按钮**之前**把整条命令原样摆给用户看 ——
    /// 一条会在生产机上跑起来的命令,值得让人过一眼。
    /// </para>
    /// </summary>
    /// <param name="spec">表单值。</param>
    /// <returns>完整命令行。</returns>
    public string BuildRunCommand(RunSpec spec)
    {
        List<string> parts = [D, "run"];
        if (spec.Detach)
        {
            parts.Add("-d");
        }
        if (spec.RemoveOnExit)
        {
            parts.Add("--rm");
        }
        if (spec.Name.Length > 0)
        {
            parts.Add($"--name {Sh.Quote(spec.Name)}");
        }
        foreach (var port in SplitLines(spec.Ports))
        {
            parts.Add($"-p {Sh.Quote(port)}");
        }
        foreach (var volume in SplitLines(spec.Volumes))
        {
            parts.Add($"-v {Sh.Quote(volume)}");
        }
        foreach (var env in SplitLines(spec.Environment))
        {
            parts.Add($"-e {Sh.Quote(env)}");
        }
        if (spec.Network.Length > 0)
        {
            parts.Add($"--network {Sh.Quote(spec.Network)}");
        }
        // --rm 与 --restart 互斥,docker 会直接报错。这里按 --rm 优先静默跳过重启策略:
        // 勾了"退出即删"还要求"总是重启"本就是矛盾的,报错不如按字面意思办。
        if (spec.RestartPolicy.Length > 0 && spec.RestartPolicy != "no" && !spec.RemoveOnExit)
        {
            parts.Add($"--restart {Sh.Quote(spec.RestartPolicy)}");
        }
        var extra = Sh.Raw(spec.ExtraArgs);
        if (extra.Length > 0)
        {
            parts.Add(extra);
        }
        parts.Add(Sh.Quote(spec.Image));
        var command = Sh.Raw(spec.Command);
        if (command.Length > 0)
        {
            parts.Add(command);
        }
        return string.Join(' ', parts);
    }

    /// <summary>按表单跑一个容器。</summary>
    /// <param name="spec">表单值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public async Task<ExecResult> RunContainerAsync(RunSpec spec, CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync(BuildRunCommand(spec), LongTimeout, cancellationToken).ConfigureAwait(false);
        return result with { Output = OutputText.Collapse(result.Output) };
    }

    /// <summary>按行切开多行输入(去空白行)。</summary>
    /// <param name="text">多行文本。</param>
    /// <returns>逐行值。</returns>
    internal static IEnumerable<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
