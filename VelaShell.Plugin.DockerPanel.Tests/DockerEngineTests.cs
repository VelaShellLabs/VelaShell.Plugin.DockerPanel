using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 执行引擎:命令包装、探测、失败归类。
/// <para>
/// SDK 1.1 起标准错误与退出码由宿主如实带回,所以这一层不再有"哨兵"那套东西
/// (<c>2&gt;&amp;1; printf __RC__</c>)。剩下的 <c>sh -c</c> 只为设环境变量 ——
/// 用户的登录 shell 可能是 fish 或 csh,那里 <c>VAR=v cmd</c> 这种前缀赋值不成立。
/// </para>
/// </summary>
[TestClass]
public sealed class DockerEngineTests
{
    private static DockerEngine NewEngine(TestPluginContext context, string sessionId = "s1") => new(context, sessionId);

    [TestMethod]
    public void Wrap_RunsUnderShAndExportsLocale()
    {
        using TestPluginContext context = new();
        var wrapped = NewEngine(context).Wrap("docker ps");
        StringAssert.StartsWith(wrapped, "sh -c '");
        StringAssert.Contains(wrapped, "docker ps");
        StringAssert.Contains(wrapped, "export LC_ALL=C");
    }

    [TestMethod]
    public void Wrap_NoLongerMergesStreamsOrAppendsAnExitSentinel()
    {
        using TestPluginContext context = new();
        var wrapped = NewEngine(context).Wrap("docker ps");
        // 合并两条流会让解析 --format json 的代码被一行 WARNING 噎死;
        // 哨兵在 fish/csh 下更是直接崩。两样现在都由 SDK 如实提供,不该再出现在命令里。
        Assert.IsFalse(wrapped.Contains("2>&1", StringComparison.Ordinal), wrapped);
        Assert.IsFalse(wrapped.Contains("__VELA_DOCKER_RC", StringComparison.Ordinal), wrapped);
    }

    [TestMethod]
    public void Wrap_ExportsDockerHostWhenConfigured()
    {
        using TestPluginContext context = new();
        var engine = NewEngine(context);
        engine.DockerHost = "tcp://10.0.0.5:2375";
        StringAssert.Contains(RemoteScript.Unwrap(engine.Wrap("docker ps")), "export DOCKER_HOST='tcp://10.0.0.5:2375'");
    }

    [TestMethod]
    public async Task RunAsync_PassesStandardErrorAndExitCodeStraightThrough()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.ResultHandler = (_, _) =>
            new("") { Error = "Error response from daemon: no such container", ExitCode = 1 };

        var result = await NewEngine(context).RunAsync("docker stop nope", null, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, result.ExitCode);
        StringAssert.Contains(result.FailureText, "no such container");
    }

    [TestMethod]
    public async Task RunAsync_ReportsFailureInsteadOfThrowing_WhenSessionIsGone()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => throw new VelaShell.PluginSdk.PluginSessionNotFoundException("gone");
        var result = await NewEngine(context).RunAsync("docker ps", null, CancellationToken.None);
        // 界面上的每个动作都是一条命令;抛出去只会变成"点了没反应"。
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(-1, result.ExitCode);
    }

    [TestMethod]
    public async Task RunSectionsAsync_SplitsOutputPerCommand()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => "one\n__VELA_DOCKER_SECTION__\ntwo";
        var sections = await NewEngine(context)
                                               .RunSectionsAsync(["a", "b"], null, CancellationToken.None);
        Assert.AreEqual(2, sections.Count);
        Assert.AreEqual("one", sections[0]);
        Assert.AreEqual("two", sections[1]);
    }

    [TestMethod]
    public async Task RunSectionsAsync_StillMergesStderrBecauseSectionsNeedOneStream()
    {
        using TestPluginContext context = new();
        var seen = string.Empty;
        context.FakeRemoteExec.Handler = (_, command) =>
        {
            seen = RemoteScript.Unwrap(command);
            return string.Empty;
        };
        await NewEngine(context).RunSectionsAsync(["a", "b"], null, CancellationToken.None);
        // 分段是靠在**一条**流里插哨兵实现的:两条流各自到达就没法把错误归到正确的段上。
        // 分段只用于探测,那时"这一段说了什么"比"它说在哪条流上"重要。
        StringAssert.Contains(seen, "2>&1");
    }

    [TestMethod]
    public async Task StreamAsync_ForwardsEachLineInOrder()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.StreamHandler = (_, _) =>
        [
            new(ExecStream.StandardOutput, "first"),
            new(ExecStream.StandardError, "a warning"),
            new(ExecStream.StandardOutput, "second")
        ];
        List<string> lines = [];
        var result = await NewEngine(context)
                                        .StreamAsync("docker logs -f web", o => lines.Add(o.Line), CancellationToken.None);
        Assert.AreEqual(3, result.Lines);
        // 顺序就是日志的全部意义 —— 引擎的接收器是同步转发的,不是 System.Progress<T>。
        CollectionAssert.AreEqual((string[])["first", "a warning", "second"], lines.ToArray());
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
    public async Task ProbeAsync_ReadsServerVersionAndComposeFlavour()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => string.Join("\n__VELA_DOCKER_SECTION__\n",
        [
            "27.3.1",                                  // client
            "27.3.1",                                  // server
            "v2.29.7",                                 // docker compose
            "sh: docker-compose: not found"            // 独立二进制没有
        ]);
        var engine = NewEngine(context);
        var probe = await engine.ProbeAsync(CancellationToken.None);
        Assert.IsTrue(probe.IsUsable);
        Assert.AreEqual("27.3.1", probe.ServerVersion);
        Assert.IsTrue(probe.HasCompose);
        Assert.AreEqual("compose", probe.ComposeCommand);
        Assert.AreEqual("docker compose", engine.ComposePrefix);
        Assert.IsTrue(engine.SupportsProjectListing);
    }

    [TestMethod]
    public async Task ProbeAsync_FallsBackToStandaloneCompose()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => string.Join("\n__VELA_DOCKER_SECTION__\n",
        [
            "20.10.24",
            "20.10.24",
            "docker: 'compose' is not a docker command.",
            "1.29.2"
        ]);
        var engine = NewEngine(context);
        var probe = await engine.ProbeAsync(CancellationToken.None);
        Assert.AreEqual(DockerEngine.StandaloneCompose, probe.ComposeCommand);
        Assert.AreEqual("docker-compose", engine.ComposePrefix);
        // v1 没有 `ls` 子命令,项目列表因此列不出来 —— 界面据此改推「打开文件…」。
        Assert.IsFalse(engine.SupportsProjectListing);
    }

    [TestMethod]
    public async Task ProbeAsync_ReportsPermissionDeniedAsSomethingSudoCanFix()
    {
        using TestPluginContext context = new();
        context.FakeRemoteExec.Handler = (_, _) => string.Join("\n__VELA_DOCKER_SECTION__\n",
        [
            "27.3.1",
            "permission denied while trying to connect to the Docker daemon socket",
            "",
            ""
        ]);
        var probe = await NewEngine(context).ProbeAsync(CancellationToken.None);
        Assert.IsFalse(probe.IsUsable);
        Assert.AreEqual("denied", probe.Diagnostic);
    }

    [TestMethod]
    public void SudoPrefix_AppliesToDocker()
    {
        using TestPluginContext context = new();
        var engine = NewEngine(context);
        engine.UseSudo = true;
        Assert.AreEqual("sudo -n docker", engine.DockerPrefix);
    }
}
