using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>系统页一次刷新拿到的三块文本。</summary>
/// <param name="Version">docker version 的输出。</param>
/// <param name="Info">docker info 的输出。</param>
/// <param name="DiskUsage">docker system df -v 的输出。</param>
internal sealed record SystemSnapshot(string Version, string Info, string DiskUsage);

/// <summary>可回收的资源类别。</summary>
internal enum PruneKind
{
    /// <summary>已停止的容器。</summary>
    Containers,

    /// <summary>镜像(默认只清悬空的)。</summary>
    Images,

    /// <summary>没有容器引用的卷。</summary>
    Volumes,

    /// <summary>没有容器使用的网络。</summary>
    Networks,

    /// <summary>构建缓存。</summary>
    BuildCache,

    /// <summary>以上全部(<c>docker system prune</c>)。</summary>
    All
}

internal sealed partial class DockerApi
{
    /// <summary>一次往返取回系统页的三块文本。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>三块文本。</returns>
    public async Task<SystemSnapshot> SystemSnapshotAsync(CancellationToken cancellationToken)
    {
        var sections = await Engine.RunSectionsAsync(
        [
            $"{D} version",
            $"{D} info",
            $"{D} system df -v"
        ], TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        return new(
            sections.ElementAtOrDefault(0) ?? string.Empty,
            sections.ElementAtOrDefault(1) ?? string.Empty,
            sections.ElementAtOrDefault(2) ?? string.Empty);
    }

    /// <summary>磁盘占用汇总(系统页顶部那一行的数据源)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><c>docker system df</c> 的表格文本。</returns>
    public async Task<string> DiskUsageAsync(CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync($"{D} system df", TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>拼一条 prune 命令(界面在确认框里把它原样摆出来)。</summary>
    /// <param name="kind">类别。</param>
    /// <param name="allImages">镜像类别下连"没有容器在用的有标签镜像"也清(<c>-a</c>)—— 这是危险得多的一档。</param>
    /// <param name="withVolumes">"全部"类别下连卷一起清(<c>--volumes</c>)—— 这一档会**删数据**。</param>
    /// <returns>完整命令行。</returns>
    public string BuildPruneCommand(PruneKind kind, bool allImages, bool withVolumes) => kind switch
    {
        PruneKind.Containers => $"{D} container prune -f",
        PruneKind.Images => $"{D} image prune -f{(allImages ? " -a" : "")}",
        PruneKind.Volumes => $"{D} volume prune -f",
        PruneKind.Networks => $"{D} network prune -f",
        PruneKind.BuildCache => $"{D} builder prune -f",
        _ => $"{D} system prune -f{(allImages ? " -a" : "")}{(withVolumes ? " --volumes" : "")}"
    };

    /// <summary>执行一次回收。</summary>
    /// <param name="kind">类别。</param>
    /// <param name="allImages">镜像连有标签的一起清。</param>
    /// <param name="withVolumes">连卷一起清。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public async Task<ExecResult> PruneAsync(PruneKind kind, bool allImages, bool withVolumes, CancellationToken cancellationToken)
    {
        var result = await Engine
                                    .RunAsync(BuildPruneCommand(kind, allImages, withVolumes), LongTimeout, cancellationToken)
                                    .ConfigureAwait(false);
        return result with { Output = OutputText.Collapse(result.Output) };
    }

    /// <summary>
    /// 概览计数:容器(总/在跑)、镜像、卷、网络。
    /// 一次 exec 四个数,给标题栏那一行用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>四个计数;取不到的为 -1。</returns>
    public async Task<(int Containers, int Running, int Images, int Volumes)> CountsAsync(CancellationToken cancellationToken)
    {
        var sections = await Engine.RunSectionsAsync(
        [
            $"{D} ps -aq | wc -l",
            $"{D} ps -q | wc -l",
            $"{D} images -q | wc -l",
            $"{D} volume ls -q | wc -l"
        ], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        return (ParseCount(sections, 0), ParseCount(sections, 1), ParseCount(sections, 2), ParseCount(sections, 3));
    }

    private static int ParseCount(IReadOnlyList<string> sections, int index)
    {
        var text = (sections.ElementAtOrDefault(index) ?? string.Empty).Trim();
        foreach (var line in text.Split('\n'))
        {
            if (int.TryParse(line.Trim(), out var value))
            {
                return value;
            }
        }
        return -1;
    }
}
