using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.RemoteExec;
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

    /// <summary>
    /// 假的标准输出。
    /// <para>
    /// 从前这里还要给输出补一个退出码哨兵(<c>__VELA_DOCKER_RC:0__</c>)——
    /// SDK 1.1 起退出码由宿主如实带回,那套东西连同它的解析代码一起没了。
    /// </para>
    /// </summary>
    /// <param name="output">标准输出。</param>
    /// <returns>原样返回。</returns>
    private static string Out(string output) => output;

    [TestMethod]
    public void BuildRunCommand_QuotesEveryUserSuppliedValue()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var command = api.BuildRunCommand(new()
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
        (var api, var context) = NewApi();
        using (context)
        {
            // --rm 与 --restart 互斥,docker 会直接报错。勾了"退出即删"还要求"总是重启"
            // 本就是矛盾的;按字面意思办比丢一条错误给用户强。
            var command = api.BuildRunCommand(new()
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
        (var api, var context) = NewApi();
        using (context)
        {
            var command = api.BuildRunCommand(new() { Image = "alpine", ExtraArgs = "--cpus 1 --memory 512m" });
            // 那一栏的用途就是"我要自己写参数";引用了反而什么都传不进去。
            StringAssert.Contains(command, "--cpus 1 --memory 512m");
        }
    }

    [TestMethod]
    public void BuildPruneCommand_MatchesEachTier()
    {
        (var api, var context) = NewApi();
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
        (var api, var context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => Out(
                string.Join("\n__VELA_DOCKER_SECTION__\n", ["27.3.1", "27.3.1", "v2.29.7", ""]));
            await api.Engine.ProbeAsync(CancellationToken.None);

            var command = api.BuildComposeCommand("app", "/srv/app/docker-compose.yml", "up -d");
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
        (var api, var context) = NewApi();
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
        (var api, var context) = NewApi();
        using (context)
        {
            var command = api.BuildExecCommand("abc123", "bash", "root", "/app");
            StringAssert.Contains(command, "docker exec -it -u 'root' -w '/app' 'abc123' bash");
            // alpine / distroless 派生里没有 bash;先试再退回,比让用户吃一句
            // "executable file not found" 再自己改一遍强。
            StringAssert.Contains(command, "|| docker exec -it -u 'root' -w '/app' 'abc123' sh");
        }
    }

    [TestMethod]
    public async Task ListContainersAsync_ParsesAndPutsRunningFirst()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => Out("""
                {"ID":"aaa","Names":"stopped-one","Image":"alpine","State":"exited","Status":"Exited (0) 2 hours ago"}
                {"ID":"bbb","Names":"web","Image":"nginx","State":"running","Status":"Up 3 minutes"}
                """);
            (var items, var result) =
                await api.ListContainersAsync(true, false, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(2, items.Count);
            // 停掉的容器混在中间不好扫;在跑的提到前面。
            Assert.AreEqual("web", items[0].Name);
            Assert.AreEqual("stopped-one", items[1].Name);
        }
    }

    [TestMethod]
    public async Task ListContainersAsync_AsksForAllAndSizeOnlyWhenRequested()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return Out(string.Empty);
            };
            await api.ListContainersAsync(false, false, CancellationToken.None);
            Assert.IsFalse(seen.Contains("ps -a", StringComparison.Ordinal), seen);
            Assert.IsFalse(seen.Contains(" -s ", StringComparison.Ordinal), seen);

            await api.ListContainersAsync(true, true, CancellationToken.None);
            StringAssert.Contains(seen, "ps -a -s");
        }
    }

    [TestMethod]
    public async Task RunBatchAsync_SplitsSuccessesFromFailuresInOneRoundTrip()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            // docker 把成功的目标原样回显到 stdout,失败的写到 stderr —— 一次往返就够分辨。
            context.FakeRemoteExec.ResultHandler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return new("aaa") { Error = "Error response from daemon: cannot stop container bbb", ExitCode = 1 };
            };
            var outcomes =
                await api.ContainerActionAsync("stop", ["aaa", "bbb"], CancellationToken.None);

            // 一条命令带全部目标:N 个容器 = 1 次往返,而不是 N 次。
            StringAssert.Contains(seen, "docker stop 'aaa' 'bbb'");
            Assert.AreEqual(1, context.FakeRemoteExec.Executed.Count);
            // "停了一个、另一个失败"必须分得清 —— 把整批说成"失败"是**假的**:aaa 确实停了。
            Assert.AreEqual(2, outcomes.Count);
            Assert.IsTrue(outcomes[0].IsSuccess);
            Assert.IsFalse(outcomes[1].IsSuccess);
            StringAssert.Contains(outcomes[1].Output, "cannot stop container");
        }
    }

    [TestMethod]
    public async Task RunBatchAsync_TreatsSilentSuccessAsSuccess()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            // `docker update` 之类的子命令成功时不回显目标名。退出码 0 + 无输出 = 全成功。
            context.FakeRemoteExec.ResultHandler = (_, _) => new("");
            var outcomes =
                await api.UpdateRestartPolicyAsync(["aaa", "bbb"], "always", CancellationToken.None);
            Assert.IsTrue(outcomes.All(static o => o.IsSuccess));
        }
    }

    [TestMethod]
    public async Task RemoveContainersAsync_AddsForceAndVolumeFlagsOnlyWhenAsked()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return Out("aaa");
            };
            await api.RemoveContainersAsync(["aaa"], false, false, CancellationToken.None);
            Assert.IsFalse(seen.Contains("rm -f", StringComparison.Ordinal), seen);

            await api.RemoveContainersAsync(["aaa"], true, true, CancellationToken.None);
            StringAssert.Contains(seen, "rm -f -v 'aaa'");
        }
    }

    [TestMethod]
    public async Task LogsAsync_TakesASnapshotWithoutFollow()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return Out("hello");
            };
            await api.LogsAsync("abc", 500, true, "2024-05-01T09:00:00Z", CancellationToken.None);
            StringAssert.Contains(seen, "--timestamps");
            StringAssert.Contains(seen, "--tail 500");
            StringAssert.Contains(seen, "--since '2024-05-01T09:00:00Z'");
            // 快照就是快照:跟随走的是 StreamLogsAsync 那条真流。
            Assert.IsFalse(seen.Contains(" -f ", StringComparison.Ordinal), seen);
        }
    }

    [TestMethod]
    public async Task StreamLogsAsync_UsesRealFollow()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            context.FakeRemoteExec.StreamHandler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return [new(ExecStream.StandardOutput, "hello"), new(ExecStream.StandardError, "nginx writes here")];
            };
            List<string> lines = [];
            await api.StreamLogsAsync("abc", 200, true, lines.Add, CancellationToken.None);

            StringAssert.Contains(seen, "docker logs -f --timestamps --tail 200 'abc'");
            // 容器把日志写在 stderr 上是常态(nginx、很多 JVM 应用)——
            // 两条流都要,而且按到达顺序拼进同一片文本,那才是 `docker logs` 本来的样子。
            CollectionAssert.AreEqual((string[])["hello", "nginx writes here"], lines.ToArray());
        }
    }

    [TestMethod]
    public async Task StreamEventsAsync_ParsesDaemonEvents()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            context.FakeRemoteExec.StreamHandler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return
                [
                    new(ExecStream.StandardOutput,
                        """{"Type":"container","Action":"start","id":"abc","Actor":{"Attributes":{"name":"web"}}}"""),
                    new(ExecStream.StandardOutput, "not json at all")
                ];
            };
            List<DockerEvent> events = [];
            await api.StreamEventsAsync(events.Add, CancellationToken.None);

            StringAssert.Contains(seen, "docker events --format '{{json .}}'");
            // 解析不了的行跳过,不是整条流放弃。
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("container", events[0].Type);
            Assert.AreEqual("start", events[0].Action);
            Assert.AreEqual("web", events[0].Name);
            Assert.IsTrue(events[0].AffectsLists);
        }
    }

    [TestMethod]
    public void ParseEvent_IgnoresEventsWithoutATypeOrAction()
    {
        Assert.IsNull(DockerApi.ParseEvent("""{"Type":"container"}"""));
        Assert.IsNull(DockerApi.ParseEvent("plain text"));
        // 老 daemon 用 status 而不是 Action。
        Assert.AreEqual("die", DockerApi.ParseEvent("""{"Type":"container","status":"die","id":"x"}""")?.Action);
    }

    [TestMethod]
    public async Task StatsAsync_IndexesByShortIdSoItLinesUpWithNoTruncListings()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => Out("""
                {"ID":"0123456789ab","Name":"web","CPUPerc":"1.20%","MemUsage":"20MiB / 2GiB","MemPerc":"1.00%","PIDs":"7"}
                """);
            var stats = await api.StatsAsync(CancellationToken.None);
            Assert.IsTrue(stats.ContainsKey("0123456789ab"));
            Assert.AreEqual("1.20%", stats["0123456789ab"].CpuPercent);
        }
    }

    [TestMethod]
    public async Task ListComposeProjectsAsync_SaysSoWhenComposeIsMissing()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            (var items, var result) =
                await api.ListComposeProjectsAsync(true, CancellationToken.None);
            Assert.AreEqual(0, items.Count);
            Assert.IsFalse(result.IsSuccess);
            StringAssert.Contains(result.FailureText, "compose is not available");
        }
    }

    [TestMethod]
    public async Task SnapshotContainersAsync_TakesExactlyOneRoundTrip()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => Out(string.Join("\n__VELA_DOCKER_SECTION__\n",
            [
                """{"ID":"0123456789abcdef","Names":"web","Image":"nginx","State":"running","Status":"Up 3 minutes"}""",
                "24",
                "12",
                "31",
                "9",
                """{"ID":"0123456789ab","Name":"web","CPUPerc":"1.20%","MemUsage":"20MiB / 2GiB"}"""
            ]));
            var snapshot = await api.SnapshotContainersAsync(true, false, true, CancellationToken.None);
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
        (var api, var context) = NewApi();
        using (context)
        {
            var seen = string.Empty;
            context.FakeRemoteExec.Handler = (_, command) =>
            {
                seen = RemoteScript.Unwrap(command);
                return Out(string.Empty);
            };
            var snapshot = await api.SnapshotContainersAsync(true, false, false, CancellationToken.None);
            // `docker stats` 是这几段里最慢的一段;关掉 CPU/MEM 列就不该再付这个代价。
            Assert.IsFalse(seen.Contains("docker stats", StringComparison.Ordinal), seen);
            Assert.AreEqual(0, snapshot.Stats.Count);
        }
    }

    [TestMethod]
    public async Task ListComposeProjectsAsync_StaysQuietOnComposeV1()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => Out(
                string.Join("\n__VELA_DOCKER_SECTION__\n",
                ["20.10.24", "20.10.24", "docker: 'compose' is not a docker command.", "1.29.2"]));
            await api.Engine.ProbeAsync(CancellationToken.None);
            Assert.IsTrue(api.Engine.Probe.HasCompose);
            Assert.IsFalse(api.Engine.SupportsProjectListing);

            (var items, var result) =
                await api.ListComposeProjectsAsync(true, CancellationToken.None);
            // v1 没有 `ls`。这该表现为"列不出来",而不是每 5 秒往状态栏刷一条
            // 用户做不了任何事的 "No such command"。
            Assert.AreEqual(0, items.Count);
            Assert.IsTrue(result.IsSuccess);
        }
    }

    [TestMethod]
    public async Task CountsAsync_ReadsFourNumbersFromOneRoundTrip()
    {
        (var api, var context) = NewApi();
        using (context)
        {
            context.FakeRemoteExec.Handler = (_, _) => Out(
                string.Join("\n__VELA_DOCKER_SECTION__\n", ["24", "12", "31", "9"]));
            (var containers, var running, var images, var volumes) = await api.CountsAsync(CancellationToken.None);
            Assert.AreEqual(24, containers);
            Assert.AreEqual(12, running);
            Assert.AreEqual(31, images);
            Assert.AreEqual(9, volumes);
        }
    }
}
