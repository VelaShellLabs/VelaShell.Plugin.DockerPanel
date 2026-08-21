using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>docker 输出的解析与整理。</summary>
[TestClass]
public sealed class OutputParsingTests
{
    [TestMethod]
    public void ParseLines_ReadsNdjson()
    {
        const string output = """
            {"ID":"abc","Names":"web","State":"running"}
            {"ID":"def","Names":"db","State":"exited"}
            """;
        var rows = DockerJson.ParseLines(output);
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("web", DockerJson.Str(rows[0], "Names"));
        Assert.AreEqual("exited", DockerJson.Str(rows[1], "State"));
    }

    [TestMethod]
    public void ParseLines_SkipsWarningsMixedIntoTheStream()
    {
        // 探测走的是分段执行,那一条路上仍然合并两条流(分段靠在一条流里插哨兵);
        // 而且 docker 有时把 WARNING 直接写在 stdout 上。遇到一行不认识就整批放弃,
        // 等于在某些机器上永远列不出容器。
        const string output = """
            WARNING: No swap limit support
            {"ID":"abc","Names":"web"}
            """;
        Assert.AreEqual(1, DockerJson.ParseLines(output).Count);
    }

    [TestMethod]
    public void ParseLines_SkipsTruncatedJson()
    {
        Assert.AreEqual(0, DockerJson.ParseLines("{\"ID\":\"ab").Count);
    }

    [TestMethod]
    public void ParseArray_ReadsComposeLsOutput()
    {
        const string output = """
            [{"Name":"app","Status":"running(3)","ConfigFiles":"/srv/app/docker-compose.yml"}]
            """;
        var rows = DockerJson.ParseArray(output);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("app", DockerJson.Str(rows[0], "Name"));
    }

    [TestMethod]
    public void ParseArray_FallsBackToNdjson()
    {
        // 部分 compose 版本在 --format json 下回的仍是每行一个对象。
        Assert.AreEqual(1, DockerJson.ParseArray("{\"Name\":\"app\"}").Count);
    }

    [TestMethod]
    public void Pretty_ReformatsAndSurvivesGarbage()
    {
        StringAssert.Contains(DockerJson.Pretty("""[{"a":1}]"""), "\"a\": 1");
        Assert.AreEqual("not json", DockerJson.Pretty("not json"));
    }

    [TestMethod]
    public void Collapse_KeepsOnlyTheLastRepaintOfAProgressLine()
    {
        // docker pull 的进度条靠 \r 在同一行反复重画;不折叠就是几百 KB 的残影。
        var collapsed = OutputText.Collapse("Downloading 10%\rDownloading 50%\rDownloading 100%\ndone");
        Assert.AreEqual("Downloading 100%\ndone", collapsed);
    }

    [TestMethod]
    public void Tail_TruncatesAndSaysSo()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(static i => $"line{i}"));
        var tail = OutputText.Tail(text, 10);
        StringAssert.StartsWith(tail, "… 90 earlier line(s) omitted …");
        StringAssert.Contains(tail, "line100");
        Assert.IsFalse(tail.Contains("line50\n", StringComparison.Ordinal));
    }


    [TestMethod]
    public void ContainerItem_ProjectsComposeLabelsAndPorts()
    {
        ContainerItem item = new()
        {
            Id = "0123456789abcdef0123",
            Name = "app-web-1",
            Labels = "com.docker.compose.project=app,com.docker.compose.service=web",
            Ports = "0.0.0.0:8080->80/tcp, :::8080->80/tcp",
            State = "running",
            Status = "Up 3 minutes (unhealthy)"
        };
        Assert.AreEqual("0123456789ab", item.ShortId);
        Assert.AreEqual("app", item.ComposeProject);
        Assert.AreEqual("web", item.ComposeService);
        Assert.IsTrue(item.HasComposeProject);
        Assert.IsTrue(item.IsRunning);
        Assert.IsTrue(item.IsUnhealthy);
        // IPv6 那一半是同一个映射的另一面;两条都列出来只会把列撑爆。
        Assert.AreEqual("0.0.0.0:8080->80/tcp", item.PortsDisplay);
    }

    [TestMethod]
    public void ImageItem_UsesIdForDanglingImagesBecauseNoneIsNotAReference()
    {
        ImageItem dangling = new() { Id = "sha256:0123456789abcdef", Repository = "<none>", Tag = "<none>" };
        Assert.IsTrue(dangling.IsDangling);
        Assert.AreEqual("0123456789ab", dangling.Reference);

        ImageItem tagged = new() { Id = "sha256:0123456789abcdef", Repository = "nginx", Tag = "1.27" };
        Assert.IsFalse(tagged.IsDangling);
        Assert.AreEqual("nginx:1.27", tagged.Reference);
    }

    [TestMethod]
    public void NetworkItem_KnowsTheThreeUndeletableBuiltIns()
    {
        Assert.IsTrue(new NetworkItem { Id = "1", Name = "bridge" }.IsBuiltIn);
        Assert.IsTrue(new NetworkItem { Id = "2", Name = "host" }.IsBuiltIn);
        Assert.IsTrue(new NetworkItem { Id = "3", Name = "none" }.IsBuiltIn);
        Assert.IsFalse(new NetworkItem { Id = "4", Name = "app-net" }.IsBuiltIn);
    }

    [TestMethod]
    public void ComposeProjectItem_TakesTheFirstConfigFile()
    {
        ComposeProjectItem project = new()
        {
            Name = "app",
            Status = "running(2)",
            ConfigFiles = "/srv/app/docker-compose.yml,/srv/app/docker-compose.override.yml"
        };
        Assert.AreEqual("/srv/app/docker-compose.yml", project.PrimaryConfigFile);
        Assert.IsTrue(project.IsRunning);
    }

    [TestMethod]
    public void StatsItem_ParsesPercentages()
    {
        StatsItem stats = new() { Id = "abc", CpuPercent = "12.34%", MemPercent = "3.10%" };
        Assert.AreEqual(12.34, stats.CpuValue, 0.001);
        Assert.AreEqual(3.10, stats.MemValue, 0.001);
        Assert.AreEqual(0, new StatsItem { Id = "x", CpuPercent = "--" }.CpuValue);
    }
}
