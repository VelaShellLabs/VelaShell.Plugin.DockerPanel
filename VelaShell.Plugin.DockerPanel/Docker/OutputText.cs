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
        foreach (var line in raw.Split('\n'))
        {
            var carriage = line.LastIndexOf('\r');
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
        var lines = text.Split('\n');
        if (lines.Length <= maxLines)
        {
            return text;
        }
        var dropped = lines.Length - maxLines;
        return $"… {dropped} earlier line(s) omitted …\n{string.Join('\n', lines[dropped..])}";
    }
}
