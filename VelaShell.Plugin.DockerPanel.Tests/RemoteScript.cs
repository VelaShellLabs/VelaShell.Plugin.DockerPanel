namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 测试用的解包器:把引擎交给远程执行能力的那一行(<c>sh -c '…'</c>)还原成里面的脚本。
/// <para>
/// 断言必须对着**里面**那段脚本写。外层单引号会把脚本里每一个单引号变成 <c>'\''</c>,
/// 于是 <c>--since '2024-…'</c> 在最终命令行里长成 <c>--since '\''2024-…'\''</c> ——
/// 直接对着最终命令行断言,写出来的期望值没人看得懂,而且会把"引用正确"误判成失败。
/// </para>
/// </summary>
internal static class RemoteScript
{
    private const string Prefix = "sh -c '";

    /// <summary>还原出内层脚本;不是预期形状时原样返回(断言会因此失败并给出可读的原文)。</summary>
    /// <param name="wrapped">交给远程执行能力的完整命令行。</param>
    /// <returns>内层脚本。</returns>
    public static string Unwrap(string wrapped)
    {
        if (!wrapped.StartsWith(Prefix, StringComparison.Ordinal) || !wrapped.EndsWith('\''))
        {
            return wrapped;
        }
        return wrapped[Prefix.Length..^1].Replace("'\\''", "'", StringComparison.Ordinal);
    }
}
