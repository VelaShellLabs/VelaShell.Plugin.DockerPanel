using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 面板里那些**不需要一个 Avalonia 应用**就能验的逻辑:行的身份合并、确认闸门、表单。
/// <para>
/// 视图模型整体没有在这里跑:它的属性通知会走 <c>Dispatcher.UIThread</c>,而单测进程里
/// 没有 Avalonia 的调度循环 —— 硬跑只会得到一堆永远不投递的通知,测出来的东西也不可信。
/// 真正的界面行为在宿主里手测(README 的"验证清单"),这里覆盖的是它下面那层纯逻辑。
/// </para>
/// </summary>
[TestClass]
public sealed class PanelLogicTests
{
    private static ContainerItem Container(string id, string name, string state = "running") =>
        new() { Id = id, Name = name, State = state, Status = state == "running" ? "Up 1 minute" : "Exited (0)" };

    [TestMethod]
    public void RowSync_KeepsTheSameRowInstanceWhenOnlyTheDataChanged()
    {
        System.Collections.ObjectModel.ObservableCollection<ContainerRow> rows = [];
        RowSync.Apply(rows, [Container("a", "web")], static c => c.Id, static c => new ContainerRow(c));
        ContainerRow first = rows[0];

        // 刷新时 Status 天天在变(Up 1 minute → Up 2 minutes)。换掉行实例的话,
        // 用户正选着的那一行每几秒就会被 ListBox 丢掉一次选中态。
        RowSync.Apply(rows, [Container("a", "web") with { Status = "Up 2 minutes" }],
            static c => c.Id, static c => new ContainerRow(c));

        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(first, rows[0]);
        Assert.AreEqual("Up 2 minutes", rows[0].Model.Status);
    }

    [TestMethod]
    public void RowSync_InsertsRemovesAndReorders()
    {
        System.Collections.ObjectModel.ObservableCollection<ContainerRow> rows = [];
        RowSync.Apply(rows, [Container("a", "web"), Container("b", "db")],
            static c => c.Id, static c => new ContainerRow(c));
        ContainerRow db = rows[1];

        // 顺序反过来 + 多一个 + 少一个。
        RowSync.Apply(rows, [Container("b", "db"), Container("c", "cache")],
            static c => c.Id, static c => new ContainerRow(c));

        Assert.AreEqual(2, rows.Count);
        Assert.AreSame(db, rows[0], "已有的行应该被搬过去而不是重建");
        Assert.AreEqual("c", rows[1].Key);
    }

    [TestMethod]
    public void ContainerRow_ClearsStatsWhenTheContainerIsNoLongerRunning()
    {
        ContainerRow row = new(Container("a", "web"));
        row.ApplyStats(new() { Id = "a", CpuPercent = "5.00%", MemUsage = "20MiB / 2GiB" });
        Assert.AreEqual("5.00%", row.Cpu);

        // 容器停了之后 stats 里就没有它了。留着上一次的数字比留空更糟 ——
        // 那是一个看起来还在跑的死容器。
        row.ApplyStats(null);
        Assert.AreEqual(string.Empty, row.Cpu);
        Assert.AreEqual(string.Empty, row.Memory);
    }

    [TestMethod]
    public async Task Confirmation_ReturnsTheAnswerAndTheOptionCheckbox()
    {
        Confirmation confirm = new();
        Task<ConfirmAnswer> pending = confirm.AskAsync(
            "Remove 2 containers?", "…", "docker rm a b", "Remove", "Cancel", true, optionLabel: "with volumes");
        Assert.IsTrue(confirm.IsOpen);
        Assert.IsTrue(confirm.HasOption);
        confirm.OptionValue = true;
        confirm.ConfirmCommand.Execute(null);

        ConfirmAnswer answer = await pending;
        Assert.IsTrue(answer.Confirmed);
        Assert.IsTrue(answer.Option);
        Assert.IsFalse(confirm.IsOpen);
    }

    [TestMethod]
    public async Task Confirmation_RequiresTheTypedPhraseForDataLosingActions()
    {
        Confirmation confirm = new();
        Task<ConfirmAnswer> pending = confirm.AskAsync(
            "Delete 3 volumes?", "…", "docker volume rm …", "Delete", "Cancel", true, "delete", "Type delete to confirm");
        Assert.IsTrue(confirm.RequiresTyping);
        Assert.IsFalse(confirm.CanConfirm);

        confirm.TypedText = "delet";
        Assert.IsFalse(confirm.CanConfirm, "差一个字符也不算");
        confirm.ConfirmCommand.Execute(null);
        Assert.IsTrue(confirm.IsOpen, "确认按钮不可用时点击必须什么都不做");

        confirm.TypedText = " delete ";
        Assert.IsTrue(confirm.CanConfirm, "首尾空白不该成为障碍");
        confirm.ConfirmCommand.Execute(null);
        Assert.IsTrue((await pending).Confirmed);
    }

    [TestMethod]
    public async Task Confirmation_RefusesASecondQuestionWhileOneIsStillOnScreen()
    {
        Confirmation confirm = new();
        Task<ConfirmAnswer> first = confirm.AskAsync("A?", "", "", "OK", "Cancel");
        ConfirmAnswer second = await confirm.AskAsync("B?", "", "", "OK", "Cancel");
        // 排队会让用户在第一个框上点完"确认"之后,莫名其妙地被问第二个他早已忘了的问题 ——
        // 而这里的每个问题都关乎删东西。
        Assert.IsFalse(second.Confirmed);
        confirm.CancelCommand.Execute(null);
        Assert.IsFalse((await first).Confirmed);
    }

    [TestMethod]
    public async Task PanelForm_CollectsValuesAndKeepsThePreviewLive()
    {
        PanelForm form = new();
        FormField image = PanelForm.Text("image", "Image", "nginx");
        FormField all = PanelForm.Boolean("allTags", "All tags");
        Task<IReadOnlyDictionary<string, string>?> pending = form.AskAsync(
            "Pull", string.Empty, [image, all], "Pull", "Cancel", "Will run",
            v => $"docker pull{(v.Flag("allTags") ? " -a" : "")} {v.Text("image")}");

        Assert.AreEqual("docker pull nginx", form.PreviewText);
        // 预览必须跟着输入走:用户按下"执行"之前看到的那条命令,就是会跑起来的那条。
        all.BoolValue = true;
        image.Value = "redis:7";
        Assert.AreEqual("docker pull -a redis:7", form.PreviewText);

        form.SubmitCommand.Execute(null);
        IReadOnlyDictionary<string, string>? values = await pending;
        Assert.IsNotNull(values);
        Assert.AreEqual("redis:7", values.Text("image"));
        Assert.IsTrue(values.Flag("allTags"));
    }

    [TestMethod]
    public async Task PanelForm_CancelReturnsNullSoCallersCanTellItApartFromEmptyInput()
    {
        PanelForm form = new();
        Task<IReadOnlyDictionary<string, string>?> pending =
            form.AskAsync("Rename", string.Empty, [PanelForm.Text("name", "Name")], "OK", "Cancel");
        form.CancelCommand.Execute(null);
        Assert.IsNull(await pending);
    }

    [TestMethod]
    public void FormField_ChoiceStartsOnItsDefaultAndWritesBackTheValue()
    {
        FormField policy = PanelForm.Choice("policy", "Policy",
        [
            new("no", "no"),
            new("always", "always")
        ], "always");
        Assert.AreEqual("always", policy.Value);
        Assert.AreEqual("always", policy.SelectedChoice?.Value);

        policy.SelectedChoice = policy.Choices[0];
        Assert.AreEqual("no", policy.Value);
    }
}
