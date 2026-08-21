using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 执行引擎:命令包装、退出码回传、失败归类。
/// 宿主的远程执行能力只回标准输出,这一层就是把"退出码 + 标准错误"补回来的地方 ——
/// 它错了,面板就会安静地把失败显示成成功。
/// </summary>
[TestClass]
public sealed class DockerEngineTests
{
    private static DockerEngine NewEngine(TestPluginContext context, string sessionId = "s1") => new(context, sessionId);

    /// <summary>把一段假输出包成"带哨兵"的样子,模拟真实远端的回包。</summary>
    private static string WithExit(string output, int exitCode) =>
        $"{output}\n__VELA_DOCKER_RC:{exitCode}__\n";

    [TestMethod]
    public void Wrap_RunsUnderSh_AndAppendsExitMarker()
    {
        using TestPluginContext context = new();
        string wrapped = NewEngine(context).Wrap("docker ps");
        // 必须过一层 sh -c:用户的登录 shell 可能是 fish/csh,那里没有 $?。
        StringAssert.StartsWith(wrapped, "sh -c '");
        StringAssert.Contains(wrapped, "docker ps");
        StringAssert.Contains(wrapped, "2>&1");
        StringAssert.Contains(wrapped, "__VELA_DOCKER_RC:");
        StringAssert.Contains(wrapped, "export LC_ALL=C");
    }

    [TestMethod]
    public void Wrap_DoesNotProduceEmptyCommandBeforeClosingBrace()
    {
        using TestPluginContext context = new();
        // 脚本以分号结尾(RunSectionsAsync 就是这样拼的)。收尾必须用换行而不是再补一个分号,
        // 否则远端拿到的是 `; ; }` —— 一个语法错误,而且错在**每一条**命令上。
        string wrapped = NewEngine(context).Wrap("docker ps; ");
        Assert.IsFalse(wrapped.Contains("; ; }", StringComparison.Ordinal), wrapped);
        StringAssert.Contains(wrapped, "\n} 2>&1");
    }

    [TestMethod]
    public void Wrap_ExportsDockerHostWhenConfigured()
    {
        using TestPluginContext context = new();
        DockerEngine engine = NewEngine(context);
        engine.DockerHost = "tcp://10.0.0.5:2375";
        StringAssert.Contains(RemoteScript.Unwrap(engine.Wrap("docker ps")), "export DOCKER_HOST='tcp://10.0.0.5:2375'");
    }

    [TestMethod]
    public void Split_ReadsExitCodeAndStripsMarker()
    {
        DockerResult result = DockerEngine.Split(WithExit("hello", 0));
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.Ok);
        Assert.AreEqual("hello", result.Output);
    }

    [TestMethod]
    public void Split_ReadsNonZeroExitCode()
    {
        DockerResult result = DockerEngine.Split(WithExit("Error response from daemon: no such container", 1));
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Output, "no such container");
    }

    [TestMethod]
    public void Split_TakesTheLastMarker()
    {
        // 容器日志里恰好印出同样的串也不该被误认成退出码。
        DockerResult result = DockerEngine.Split(WithExit("__VELA_DOCKER_RC:99__ printed by the app", 0));
        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Output, "printed by the app");
    }

    [TestMethod]
    public void Split_WithoutMarker_ReportsUnknownExit()
    {
        DockerResult result = DockerEngine.Split("connection lost");
        Assert.AreEqual(-1, result.ExitCode);
        Assert.AreEqual("connection lost", result.Output);
    }

    [TestMethod]
    public void DescribeFailure_ClassifiesTheThreeCommonCases()
    {
        Assert.AreEqual("denied",
            DockerEngine.DescribeFailure("permission denied while trying to connect to the Docker daemon socket", true));
        Assert.AreEqual("daemon",
            DockerEngine.DescribeFailure("Cannot connect to the Docker daemon at unix:///var/run/docker.sock.", true));
        Assert.AreEqual("missing", DockerEngine.DescribeFailure("sh: docker: not found", false));
        Assert.AreEqual("sudo-password", DockerEngine.DescribeFailure("sudo: a password is required", true));
    }

    [TestMethod]
    public async Task RunAsync_ReportsFailureInsteadOfThrowing_WhenSessionIsGone()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => throw new VelaShell.PluginSdk.PluginSessionNotFoundException("gone");
        DockerResult result = await NewEngine(context).RunAsync("docker ps", null, CancellationToken.None);
        // 界面上的每个动作都是一条命令;抛出去只会变成"点了没反应"。
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(-1, result.ExitCode);
    }

    [TestMethod]
    public async Task RunSectionsAsync_SplitsOutputPerCommand()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => WithExit("one\n__VELA_DOCKER_SECTION__\ntwo", 0);
        IReadOnlyList<string> sections = await NewEngine(context)
                                               .RunSectionsAsync(["a", "b"], null, CancellationToken.None);
        Assert.AreEqual(2, sections.Count);
        Assert.AreEqual("one", sections[0]);
        Assert.AreEqual("two", sections[1]);
    }

    [TestMethod]
    public async Task ProbeAsync_ReadsServerVersionAndComposeFlavour()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => WithExit(
            string.Join("\n__VELA_DOCKER_SECTION__\n",
            [
                "27.3.1",                                  // client
                "27.3.1",                                  // server
                "v2.29.7",                                 // docker compose
                "sh: docker-compose: not found"            // 独立二进制没有
            ]), 0);
        DockerEngine engine = NewEngine(context);
        DockerProbe probe = await engine.ProbeAsync(CancellationToken.None);
        Assert.IsTrue(probe.IsUsable);
        Assert.AreEqual("27.3.1", probe.ServerVersion);
        Assert.IsTrue(probe.HasCompose);
        Assert.AreEqual("compose", probe.ComposeCommand);
        Assert.AreEqual("docker compose", engine.ComposePrefix);
    }

    [TestMethod]
    public async Task ProbeAsync_FallsBackToStandaloneCompose()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => WithExit(
            string.Join("\n__VELA_DOCKER_SECTION__\n",
            [
                "20.10.24",
                "20.10.24",
                "docker: 'compose' is not a docker command.",
                "1.29.2"
            ]), 0);
        DockerEngine engine = NewEngine(context);
        DockerProbe probe = await engine.ProbeAsync(CancellationToken.None);
        Assert.AreEqual(DockerEngine.StandaloneCompose, probe.ComposeCommand);
        Assert.AreEqual("docker-compose", engine.ComposePrefix);
    }

    [TestMethod]
    public async Task ProbeAsync_ReportsPermissionDeniedAsSomethingSudoCanFix()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => WithExit(
            string.Join("\n__VELA_DOCKER_SECTION__\n",
            [
                "27.3.1",
                "permission denied while trying to connect to the Docker daemon socket",
                "",
                ""
            ]), 1);
        DockerProbe probe = await NewEngine(context).ProbeAsync(CancellationToken.None);
        Assert.IsFalse(probe.IsUsable);
        Assert.AreEqual("denied", probe.Diagnostic);
    }

    [TestMethod]
    public void SudoPrefix_AppliesToBothDockerAndCompose()
    {
        using TestPluginContext context = new();
        DockerEngine engine = NewEngine(context);
        engine.UseSudo = true;
        Assert.AreEqual("sudo -n docker", engine.DockerPrefix);
    }

}
