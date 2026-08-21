using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>daemon 推来的一条事件(<c>docker events</c>)。</summary>
/// <param name="Type">对象类型(<c>container</c> / <c>image</c> / <c>volume</c> / <c>network</c>)。</param>
/// <param name="Action">发生了什么(<c>start</c> / <c>die</c> / <c>pull</c> / <c>destroy</c> …)。</param>
/// <param name="Id">对象 id。</param>
/// <param name="Name">对象名(容器名 / 镜像引用);取不到为空。</param>
internal sealed record DockerEvent(string Type, string Action, string Id, string Name)
{
    /// <summary>这条事件会改变面板上某个列表的内容(据此决定要不要刷新)。</summary>
    public bool AffectsLists => Type is "container" or "image" or "volume" or "network";
}

internal sealed partial class DockerApi
{
    /// <summary>
    /// 跟随 daemon 的事件流。
    /// <para>
    /// 这是面板"不轮询"的依据(§9:能用事件就不用定时器)。别处起了个容器、CI 推了个镜像、
    /// 某个容器 OOM 死掉 —— 界面在一秒内自己就更新了,而不是等下一个刷新周期。
    /// </para>
    /// </summary>
    /// <param name="onEvent">逐条事件回调(同步,I/O 线程)。</param>
    /// <param name="cancellationToken">取消令牌(面板关闭 / 换会话时触发)。</param>
    /// <returns>退出码与行数。</returns>
    public Task<ExecStreamResult> StreamEventsAsync(Action<DockerEvent> onEvent, CancellationToken cancellationToken) =>
        Engine.StreamAsync($"{D} events --format '{{{{json .}}}}'", output =>
        {
            if (output.Stream is ExecStream.StandardError)
            {
                return;
            }
            if (ParseEvent(output.Line) is { } parsed)
            {
                onEvent(parsed);
            }
        }, cancellationToken);

    /// <summary>
    /// 跟随一个容器的日志。
    /// <para>
    /// 这才是真正的 <c>docker logs -f</c>。SDK 1.1 之前只能按间隔用 <c>--since</c> 增量补拉,
    /// 那条路上要自己处理闭区间重复、时间戳游标、以及"关了时间戳就续不上"三件事;
    /// 现在这三样连同它们的 bug 一起没了。
    /// </para>
    /// </summary>
    /// <param name="id">容器 id。</param>
    /// <param name="tail">先补多少行历史。</param>
    /// <param name="timestamps">是否带时间戳。</param>
    /// <param name="onLine">逐行回调(同步,I/O 线程)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退出码与行数。</returns>
    public Task<ExecStreamResult> StreamLogsAsync(
        string id, int tail, bool timestamps, Action<string> onLine, CancellationToken cancellationToken)
    {
        var command = $"{D} logs -f{(timestamps ? " --timestamps" : "")} --tail {(tail > 0 ? tail : 200)} {Sh.Quote(id)}";
        // 容器把日志写在 stderr 上是常态(nginx、很多 JVM 应用),所以两条流都要,
        // 而且**不区分**地按到达顺序拼进同一片文本 —— 那正是 `docker logs` 本来的样子。
        return Engine.StreamAsync(command, output => onLine(output.Line), cancellationToken);
    }

    /// <summary>解析一行事件 JSON;不是事件就返回 null。</summary>
    /// <param name="line">一行 NDJSON。</param>
    /// <returns>事件;解析不出为 null。</returns>
    internal static DockerEvent? ParseEvent(string line)
    {
        var rows = DockerJson.ParseLines(line);
        if (rows.Count == 0)
        {
            return null;
        }
        var row = rows[0];
        var type = DockerJson.Str(row, "Type");
        var action = DockerJson.Str(row, "Action", DockerJson.Str(row, "status"));
        if (type.Length == 0 || action.Length == 0)
        {
            return null;
        }
        // Actor 是嵌套对象,ParseLines 把它按原始 JSON 文本留着 —— 这里只要里面的 name。
        var name = DockerJson.Property(DockerJson.Str(row, "Actor"), "Attributes", "name");
        return new(type, action, DockerJson.Str(row, "id", DockerJson.Str(row, "Actor")), name);
    }
}
