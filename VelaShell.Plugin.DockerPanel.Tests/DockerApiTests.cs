using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 命令拼装与列表解析。
/// <para>
/// 拼装值得逐条比对:这些字符串会原样在别人的生产机上跑起来,而拼错一个引号的后果
/// 不是"报错",是"删了别的东西"。
/// </para>
/// </summary>
[TestClass]
public sealed class DockerApiTests
{
    private static (DockerApi Api, TestPluginContext Context) NewApi()
    {
        TestPluginContext context = new();
        return (new(new(context, "s1")), context);
    }

    private static string WithExit(string output, int exitCode = 0) =>
        $"{output}\n__VELA_DOCKER_RC:{exitCode}__\n";

    [TestMethod]
    public void BuildRunCommand_QuotesEveryUserSuppliedValue()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string command = api.BuildRunCommand(new()
            {
                Image = "nginx:1.27",
                Name = "my app",
                Ports = "8080:80\n127.0.0.1:5432:5432/tcp",
                Volumes = "/srv/my data:/data:ro",
                Environment = "TZ=Asia/Shanghai\nMSG=hello world",
                Network = "app-net",
                RestartPolicy = "unless-stopped",
                Detach = true
            });
            StringAssert.Contains(command, "--name 'my app'");
            StringAssert.Contains(command, "-p '8080:80'");
            StringAssert.Contains(command, "-p '127.0.0.1:5432:5432/tcp'");
            StringAssert.Contains(command, "-v '/srv/my data:/data:ro'");
            StringAssert.Contains(command, "-e 'MSG=hello world'");
            StringAssert.Contains(command, "--network 'app-net'");
            StringAssert.Contains(command, "--restart 'unless-stopped'");
            StringAssert.Contains(command, "-d");
            StringAssert.EndsWith(command, "'nginx:1.27'");
        }
    }

    [TestMethod]
    public void BuildRunCommand_DropsRestartPolicyWhenRemoveOnExitIsSet()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            // --rm 与 --restart 互斥,docker 会直接报错。勾了"退出即删"还要求"总是重启"
            // 本就是矛盾的;按字面意思办比丢一条错误给用户强。
            string command = api.BuildRunCommand(new()
            {
                Image = "alpine",
                RemoveOnExit = true,
                RestartPolicy = "always"
            });
            StringAssert.Contains(command, "--rm");
            Assert.IsFalse(command.Contains("--restart", StringComparison.Ordinal), command);
        }
    }

    [TestMethod]
    public void BuildRunCommand_SplicesExtraArgumentsUnquoted()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string command = api.BuildRunCommand(new() { Image = "alpine", ExtraArgs = "--cpus 1 --memory 512m" });
            // 那一栏的用途就是"我要自己写参数";引用了反而什么都传不进去。
            StringAssert.Contains(command, "--cpus 1 --memory 512m");
        }
    }

    [TestMethod]
    public void BuildPruneCommand_MatchesEachTier()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            Assert.AreEqual("docker container prune -f", api.BuildPruneCommand(PruneKind.Containers, false, false));
            Assert.AreEqual("docker image prune -f", api.BuildPruneCommand(PruneKind.Images, false, false));
            Assert.AreEqual("docker image prune -f -a", api.BuildPruneCommand(PruneKind.Images, true, false));
            Assert.AreEqual("docker builder prune -f", api.BuildPruneCommand(PruneKind.BuildCache, false, false));
            Assert.AreEqual("docker system prune -f -a --volumes", api.BuildPruneCommand(PruneKind.All, true, true));
        }
    }

    [TestMethod]
    public async Task BuildComposeCommand_PinsBothProjectAndFileAndProjectDirectory()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit(
                string.Join("\n__VELA_DOCKER_SECTION__\n", ["27.3.1", "27.3.1", "v2.29.7", ""]));
            await api.Engine.ProbeAsync(CancellationToken.None);

            string command = api.BuildComposeCommand("app", "/srv/app/docker-compose.yml", "up -d");
            // -p:光给文件的话项目名会按目录名重新推导,down 掉的可能是另一个项目。
            StringAssert.Contains(command, "-p 'app'");
            // -f:光给项目名的话 compose 找不到 yml(它不记得项目从哪来)。
            StringAssert.Contains(command, "-f '/srv/app/docker-compose.yml'");
            // --project-directory:不给的话 yml 里的 ./data 会以登录目录为基准解析 —— 一个安静挂错盘的 bug。
            StringAssert.Contains(command, "--project-directory '/srv/app'");
            StringAssert.EndsWith(command, "up -d");
        }
    }

    [TestMethod]
    public void BuildComposeCommand_IsEmptyWhenComposeIsUnavailable()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            Assert.AreEqual(string.Empty, api.BuildComposeCommand("app", "/srv/app/compose.yml", "up -d"));
        }
    }

    [TestMethod]
    public void ParentDirectory_HandlesPosixPaths()
    {
        // 不能用 Path.GetDirectoryName:它在 Windows 上会把 / 换成 \。
        Assert.AreEqual("/srv/app", DockerApi.ParentDirectory("/srv/app/docker-compose.yml"));
        Assert.AreEqual("/", DockerApi.ParentDirectory("/compose.yml"));
        Assert.AreEqual(string.Empty, DockerApi.ParentDirectory("compose.yml"));
    }

    [TestMethod]
    public void BuildExecCommand_FallsBackFromBashToSh()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string command = api.BuildExecCommand("abc123", "bash", "root", "/app");
            StringAssert.Contains(command, "docker exec -it -u 'root' -w '/app' 'abc123' bash");
            // alpine / distroless 派生里没有 bash;先试再退回,比让用户吃一句
            // "executable file not found" 再自己改一遍强。
            StringAssert.Contains(command, "|| docker exec -it -u 'root' -w '/app' 'abc123' sh");
        }
    }

    [TestMethod]
    public async Task ListContainersAsync_ParsesAndPutsRunningFirst()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit("""
                {"ID":"aaa","Names":"stopped-one","Image":"alpine","State":"exited","Status":"Exited (0) 2 hours ago"}
                {"ID":"bbb","Names":"web","Image":"nginx","State":"running","Status":"Up 3 minutes"}
                """);
            (IReadOnlyList<ContainerItem> items, DockerResult result) =
                await api.ListContainersAsync(true, false, CancellationToken.None);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(2, items.Count);
            // 停掉的容器混在中间不好扫;在跑的提到前面。
            Assert.AreEqual("web", items[0].Name);
            Assert.AreEqual("stopped-one", items[1].Name);
        }
    }

    [TestMethod]
    public async Task ListContainersAsync_AsksForAllAndSizeOnlyWhenRequested()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return WithExit(string.Empty);
            };
            await api.ListContainersAsync(false, false, CancellationToken.None);
            Assert.IsFalse(seen.Contains("ps -a", StringComparison.Ordinal), seen);
            Assert.IsFalse(seen.Contains(" -s ", StringComparison.Ordinal), seen);

            await api.ListContainersAsync(true, true, CancellationToken.None);
            StringAssert.Contains(seen, "ps -a -s");
        }
    }

    [TestMethod]
    public async Task RunBatchAsync_ReportsPerTargetOutcomes()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit(
                string.Join("\n__VELA_DOCKER_SECTION__\n",
                [
                    "aaa",
                    "Error response from daemon: cannot stop container bbb"
                ]));
            IReadOnlyList<BatchOutcome> outcomes =
                await api.ContainerActionAsync("stop", ["aaa", "bbb"], CancellationToken.None);
            // 一条命令带一个退出码的话,"停了一个、另一个失败"只会显示成"失败"。
            Assert.AreEqual(2, outcomes.Count);
            Assert.IsTrue(outcomes[0].Ok);
            Assert.IsFalse(outcomes[1].Ok);
            StringAssert.Contains(outcomes[1].Output, "cannot stop container");
        }
    }

    [TestMethod]
    public async Task RemoveContainersAsync_AddsForceAndVolumeFlagsOnlyWhenAsked()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return WithExit("aaa");
            };
            await api.RemoveContainersAsync(["aaa"], false, false, CancellationToken.None);
            Assert.IsFalse(seen.Contains("rm -f", StringComparison.Ordinal), seen);

            await api.RemoveContainersAsync(["aaa"], true, true, CancellationToken.None);
            StringAssert.Contains(seen, "rm -f -v 'aaa'");
        }
    }

    [TestMethod]
    public async Task LogsAsync_NeverUsesFollowBecauseTheChannelIsOneShot()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return WithExit("hello");
            };
            await api.LogsAsync("abc", 500, true, "2024-05-01T09:00:00Z", CancellationToken.None);
            StringAssert.Contains(seen, "--timestamps");
            StringAssert.Contains(seen, "--tail 500");
            StringAssert.Contains(seen, "--since '2024-05-01T09:00:00Z'");
            // -f 永远不返回,只会挂到超时然后把整段丢掉。
            Assert.IsFalse(seen.Contains(" -f ", StringComparison.Ordinal), seen);
            Assert.IsFalse(seen.Contains("--follow", StringComparison.Ordinal), seen);
        }
    }

    [TestMethod]
    public async Task StatsAsync_IndexesByShortIdSoItLinesUpWithNoTruncListings()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit("""
                {"ID":"0123456789ab","Name":"web","CPUPerc":"1.20%","MemUsage":"20MiB / 2GiB","MemPerc":"1.00%","PIDs":"7"}
                """);
            IReadOnlyDictionary<string, StatsItem> stats = await api.StatsAsync(CancellationToken.None);
            Assert.IsTrue(stats.ContainsKey("0123456789ab"));
            Assert.AreEqual("1.20%", stats["0123456789ab"].CpuPercent);
        }
    }

    [TestMethod]
    public async Task ListComposeProjectsAsync_SaysSoWhenComposeIsMissing()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            (IReadOnlyList<ComposeProjectItem> items, DockerResult result) =
                await api.ListComposeProjectsAsync(true, CancellationToken.None);
            Assert.AreEqual(0, items.Count);
            Assert.IsFalse(result.Ok);
            StringAssert.Contains(result.Output, "compose is not available");
        }
    }

    [TestMethod]
    public async Task SnapshotContainersAsync_TakesExactlyOneRoundTrip()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit(string.Join("\n__VELA_DOCKER_SECTION__\n",
            [
                """{"ID":"0123456789abcdef","Names":"web","Image":"nginx","State":"running","Status":"Up 3 minutes"}""",
                "24",
                "12",
                "31",
                "9",
                """{"ID":"0123456789ab","Name":"web","CPUPerc":"1.20%","MemUsage":"20MiB / 2GiB"}"""
            ]));
            ContainerSnapshot snapshot = await api.SnapshotContainersAsync(true, false, true, CancellationToken.None);
            // 一次刷新 = 一次 exec。三次调用在跨洋链路上就是每次刷新多卡半秒。
            Assert.AreEqual(1, context.FakeRemoteExec.Executed.Count);
            Assert.AreEqual(1, snapshot.Containers.Count);
            Assert.AreEqual(24, snapshot.Counts.Containers);
            Assert.AreEqual(12, snapshot.Counts.Running);
            // stats 按短 id 索引,ps --no-trunc 回的是长 id —— 两边必须对得上。
            Assert.IsTrue(snapshot.Stats.ContainsKey(snapshot.Containers[0].ShortId));
        }
    }

    [TestMethod]
    public async Task SnapshotContainersAsync_SkipsTheStatsSectionWhenNotAsked()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            string seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return WithExit(string.Empty);
            };
            ContainerSnapshot snapshot = await api.SnapshotContainersAsync(true, false, false, CancellationToken.None);
            // `docker stats` 是这几段里最慢的一段;关掉 CPU/MEM 列就不该再付这个代价。
            Assert.IsFalse(seen.Contains("docker stats", StringComparison.Ordinal), seen);
            Assert.AreEqual(0, snapshot.Stats.Count);
        }
    }

    [TestMethod]
    public async Task ListComposeProjectsAsync_StaysQuietOnComposeV1()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit(
                string.Join("\n__VELA_DOCKER_SECTION__\n",
                ["20.10.24", "20.10.24", "docker: 'compose' is not a docker command.", "1.29.2"]));
            await api.Engine.ProbeAsync(CancellationToken.None);
            Assert.IsTrue(api.Engine.Probe.HasCompose);
            Assert.IsFalse(api.Engine.SupportsProjectListing);

            (IReadOnlyList<ComposeProjectItem> items, DockerResult result) =
                await api.ListComposeProjectsAsync(true, CancellationToken.None);
            // v1 没有 `ls`。这该表现为"列不出来",而不是每 5 秒往状态栏刷一条
            // 用户做不了任何事的 "No such command"。
            Assert.AreEqual(0, items.Count);
            Assert.IsTrue(result.Ok);
        }
    }

    [TestMethod]
    public async Task CountsAsync_ReadsFourNumbersFromOneRoundTrip()
    {
        (DockerApi api, TestPluginContext context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => WithExit(
                string.Join("\n__VELA_DOCKER_SECTION__\n", ["24", "12", "31", "9"]));
            (int containers, int running, int images, int volumes) = await api.CountsAsync(CancellationToken.None);
            Assert.AreEqual(24, containers);
            Assert.AreEqual(12, running);
            Assert.AreEqual(31, images);
            Assert.AreEqual(9, volumes);
        }
    }
}
