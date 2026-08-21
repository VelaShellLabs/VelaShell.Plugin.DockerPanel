namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// POSIX shell 的引用工具。
/// <para>
/// 面板里几乎每条命令都要把用户可见的字符串(容器名、镜像引用、路径、标签过滤器)
/// 拼进一段远端脚本 —— 而容器名允许句点与下划线、镜像标签允许冒号与斜杠、
/// compose 的项目目录里带空格更是家常便饭。不引用就等于把这些字符交给远端 shell 去解释,
/// 轻则命令莫名其妙失败,重则是一条注入。
/// </para>
/// <para>
/// 这里只用**单引号**这一种形式:单引号内 shell 不做任何展开($、`、\、* 全是字面量),
/// 唯一需要处理的就是单引号自身 —— 用 <c>'\''</c>(闭合、转义一个单引号、再开启)接回去。
/// 双引号形式要额外操心 <c>$</c> 与反引号,没有理由选它。
/// </para>
/// </summary>
internal static class Sh
{
    /// <summary>把一个值引用成 shell 的单个词。</summary>
    /// <param name="value">原始值(可为 null,按空串处理)。</param>
    /// <returns>可直接拼进脚本的单引号串。</returns>
    public static string Quote(string? value) =>
        $"'{(value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal)}'";

    /// <summary>把一串值逐个引用后以空格连接(参数列表用)。</summary>
    /// <param name="values">原始值序列。</param>
    /// <returns>连接好的参数串;序列为空时返回空串。</returns>
    public static string QuoteAll(IEnumerable<string> values) =>
        string.Join(' ', values.Select(Quote));

    /// <summary>
    /// 把用户在"额外参数"里手敲的一段文本原样接进命令。
    /// <para>
    /// **刻意不引用**:那一栏的用途就是"我要自己写 <c>--network host -e A=1</c>",
    /// 引用了反而什么都传不进去。这也意味着那一栏与终端里手敲同权 ——
    /// 界面上必须说清这一点,而不是假装它是一个安全的输入框。
    /// </para>
    /// </summary>
    /// <param name="raw">用户输入。</param>
    /// <returns>去掉首尾空白、把换行折成空格后的文本;为空则返回空串。</returns>
    public static string Raw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        return string.Join(' ', raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
