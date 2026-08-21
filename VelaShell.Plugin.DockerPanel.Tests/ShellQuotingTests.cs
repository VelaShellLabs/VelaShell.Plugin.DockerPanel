using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// shell 引用。这一层的每个 bug 都是同一种:一个带特殊字符的容器名/路径把命令拆成了两条。
/// </summary>
[TestClass]
public sealed class ShellQuotingTests
{
    [TestMethod]
    public void Quote_WrapsInSingleQuotes()
    {
        Assert.AreEqual("'nginx'", Sh.Quote("nginx"));
    }

    [TestMethod]
    public void Quote_LeavesShellMetacharactersInert()
    {
        // 单引号内 shell 不做任何展开:$ ` \ * ; | & 全是字面量,不需要逐个转义。
        Assert.AreEqual("'$(rm -rf /)'", Sh.Quote("$(rm -rf /)"));
        Assert.AreEqual("'a;b|c&d'", Sh.Quote("a;b|c&d"));
        Assert.AreEqual("'back\\slash'", Sh.Quote("back\\slash"));
    }

    [TestMethod]
    public void Quote_EscapesEmbeddedSingleQuote()
    {
        // 闭合 → 转义一个单引号 → 重开。这是 POSIX sh 里唯一的办法。
        Assert.AreEqual("'it'\\''s'", Sh.Quote("it's"));
    }

    [TestMethod]
    public void Quote_HandlesNullAndEmpty()
    {
        Assert.AreEqual("''", Sh.Quote(null));
        Assert.AreEqual("''", Sh.Quote(string.Empty));
    }

    [TestMethod]
    public void QuoteAll_JoinsWithSpaces()
    {
        Assert.AreEqual("'a' 'b c'", Sh.QuoteAll(["a", "b c"]));
    }

    [TestMethod]
    public void Raw_FoldsNewlinesIntoSpaces()
    {
        // "额外参数"那一栏是刻意不引用的,但换行必须折掉 —— 一个换行会把后半段
        // 变成远端 shell 的下一条命令。
        Assert.AreEqual("--cpus 1 --memory 512m", Sh.Raw("  --cpus 1\n--memory 512m  "));
        Assert.AreEqual(string.Empty, Sh.Raw("   "));
        Assert.AreEqual(string.Empty, Sh.Raw(null));
    }
}
