using System.Text;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 远端输出的文本整理。
/// <para>
/// <c>docker pull</c> / <c>compose up</c> 这类命令把进度条画在**同一行**上,靠回车
/// (<c>\r</c>)把光标拉回行首反复重画。终端里看是一条会动的进度条,但把这段字节原样
/// 塞进 <c>TextBox</c> 就是几千行 "Downloading [====&gt;   ] 12.3MB/98MB" 的残影 ——
/// 一次 pull 能顶出几百 KB 的垃圾文本,滚动条直接失去意义。
/// 这里按行只保留最后一次重画的结果,还原成"每个层一行"的样子。
/// </para>
/// </summary>
internal static class OutputText
{
    /// <summary>把回车重画折叠掉,并统一换行。</summary>
    /// <param name="raw">原始输出。</param>
    /// <returns>整理后的文本。</returns>
    public static string Collapse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!raw.Contains('\r', StringComparison.Ordinal))
        {
            return raw;
        }
        StringBuilder builder = new(raw.Length);
        foreach (string line in raw.Split('\n'))
        {
            int carriage = line.LastIndexOf('\r');
            builder.Append(carriage >= 0 ? line[(carriage + 1)..] : line).Append('\n');
        }
        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>只保留末尾若干行(日志与长输出的护栏:界面不该为一次 <c>--tail all</c> 卡住)。</summary>
    /// <param name="text">文本。</param>
    /// <param name="maxLines">最多保留的行数。</param>
    /// <returns>截断后的文本;被截断时首行给出提示。</returns>
    public static string Tail(string text, int maxLines)
    {
        if (maxLines <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }
        string[] lines = text.Split('\n');
        if (lines.Length <= maxLines)
        {
            return text;
        }
        int dropped = lines.Length - maxLines;
        return $"… {dropped} earlier line(s) omitted …\n{string.Join('\n', lines[dropped..])}";
    }

    /// <summary>
    /// 取一行日志前缀里的 RFC3339 时间戳(<c>docker logs --timestamps</c> 的格式)。
    /// 增量拉日志时要用它当下一次的 <c>--since</c>。
    /// </summary>
    /// <param name="text">整段日志。</param>
    /// <returns>最后一条日志的时间戳;找不到返回空串。</returns>
    public static string LastTimestamp(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        string[] lines = text.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            // 2024-05-01T09:12:33.123456789Z rest-of-line
            int space = line.IndexOf(' ');
            ReadOnlySpan<char> candidate = space > 0 ? line.AsSpan(0, space) : line.AsSpan();
            if (candidate.Length >= 20 && candidate[4] == '-' && candidate[7] == '-' && candidate[10] == 'T')
            {
                return candidate.ToString();
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 丢掉早于(含)某个时间戳的那些行。
    /// <para>
    /// <c>docker logs --since</c> 的边界是**闭区间**:传上一条日志的时间戳,那一条会再回来一次。
    /// 不去重的话,每次增量刷新都在日志尾部多一条重复 —— 一分钟后就是十几条。
    /// </para>
    /// </summary>
    /// <param name="text">新拉到的日志。</param>
    /// <param name="timestamp">上次已经拿到的最后一个时间戳。</param>
    /// <returns>去掉重叠部分后的日志。</returns>
    public static string DropUpTo(string text, string timestamp)
    {
        if (string.IsNullOrEmpty(text) || timestamp.Length == 0)
        {
            return text;
        }
        string[] lines = text.Split('\n');
        int keepFrom = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();
            if (line.StartsWith(timestamp, StringComparison.Ordinal))
            {
                keepFrom = i + 1;
            }
        }
        return keepFrom == 0 ? text : string.Join('\n', lines[keepFrom..]);
    }
}
