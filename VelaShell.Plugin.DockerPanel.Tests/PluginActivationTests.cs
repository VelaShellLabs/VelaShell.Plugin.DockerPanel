using System.Text.Json;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>入口与清单:插件装得上、命令进得了命令面板、停用时收得干净。</summary>
[TestClass]
public sealed class PluginActivationTests
{
    private const string OpenCommandId = "velashell.dockerpanel.open";

    [TestMethod]
    public async Task ActivateAsync_RegistersTheOpenCommand()
    {
        using TestPluginContext context = new();
        DockerPanelPlugin plugin = new();
        await plugin.ActivateAsync(context, CancellationToken.None);
        // 命令 id 必须与 plugin.json 里的占位一致 —— 不一致的话命令面板里会留下
        // 一条按了没反应的占位命令,而真正的命令换了个名字藏在旁边。
        Assert.IsTrue(context.RecordingCommands.Registered.Any(c => c.Id == OpenCommandId));
        await plugin.DeactivateAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ActivateAsync_UsesTheHostLocaleForTheCommandTitle()
    {
        using TestPluginContext context = new();
        context.HostInfo.Locale = "zh-Hans";
        DockerPanelPlugin plugin = new();
        await plugin.ActivateAsync(context, CancellationToken.None);
        var title = context.RecordingCommands.Registered.First(c => c.Id == OpenCommandId).Title;
        StringAssert.Contains(title, "Docker");
        StringAssert.Contains(title, "面板");
        await plugin.DeactivateAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LocaleChanged_ReregistersWithoutLeavingAGap()
    {
        using TestPluginContext context = new();
        context.HostInfo.Locale = "en";
        DockerPanelPlugin plugin = new();
        await plugin.ActivateAsync(context, CancellationToken.None);

        context.HostInfo.Locale = "zh-Hans";
        context.HostEvents.RaiseLocaleChanged("zh-Hans");

        // 同 id 的注册是替换而不是叠加:命令面板里只该有一条。
        Assert.AreEqual(1, context.RecordingCommands.Registered.Count(c => c.Id == OpenCommandId));
        StringAssert.Contains(context.RecordingCommands.Registered.First(c => c.Id == OpenCommandId).Title, "面板");
        await plugin.DeactivateAsync(CancellationToken.None);
    }

    [TestMethod]
    public void Manifest_MatchesWhatTheCodeRegisters()
    {
        // 清单自己解析,不借宿主的 PluginManifestReader —— 那个类型不随公开 SDK 包分发
        // (它是装载器的一部分)。这里要验的本来也不是"读得动",而是**清单与代码一致**:
        // 占位命令的 id 与实际注册的 id 对不上,用户在命令面板里按下的会是一条永远
        // 不装载插件的死命令。
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        Assert.IsTrue(File.Exists(manifestPath), $"plugin.json should ship next to the assembly: {manifestPath}");
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(manifestPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = manifest.RootElement;

        Assert.AreEqual("velashell.dockerpanel", root.GetProperty("id").GetString());
        Assert.AreEqual("VelaShell.Plugin.DockerPanel.dll", root.GetProperty("entry").GetString());
        // 面板要以停靠标签页的身份进主窗口标签区,而原生控件无法跨进程嵌入 ——
        // hostMode 必须留在默认的 inProcess(即:根本不出现在清单里)。
        Assert.IsFalse(root.TryGetProperty("hostMode", out _));

        var commands = root.GetProperty("contributes").GetProperty("commands");
        Assert.IsTrue(commands.EnumerateArray().Any(c => c.GetProperty("id").GetString() == OpenCommandId));
        // 命令 id 必须以插件 id 为前缀(宿主强制,防插件间冒名)。
        Assert.IsTrue(commands.EnumerateArray().All(
            c => c.GetProperty("id").GetString()!.StartsWith("velashell.dockerpanel.", StringComparison.Ordinal)));
        // 占位命令与惰性激活事件必须成对出现,否则要么按了不装载,要么白白在启动时装载。
        Assert.IsTrue(root.GetProperty("activationEvents").EnumerateArray()
                          .Any(e => e.GetString() == $"onCommand:{OpenCommandId}"));
    }
}
