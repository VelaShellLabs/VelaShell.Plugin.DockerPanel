using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 列表里的一行:一个**稳定的身份**外加一个会被换掉的数据模型。
/// <para>
/// 为什么不直接把 <see cref="ContainerItem" /> 这类记录塞进
/// <see cref="ObservableCollection{T}" />:面板每隔几秒就整批刷新一次,
/// 而容器的 <c>Status</c> 每次都在变(<c>Up 3 minutes</c> → <c>Up 4 minutes</c>)。
/// 记录是值相等的,内容一变就是"另一个对象",于是每次刷新都会把用户**正选着的那一行**
/// 从 <c>ListBox</c> 里换掉 —— 选中态没了,右边的详情跟着空掉。
/// 包一层身份稳定的行对象,刷新就只是"同一行的字变了"。
/// </para>
/// </summary>
/// <typeparam name="T">数据模型类型。</typeparam>
/// <param name="model">初始模型。</param>
public abstract class Row<T>(T model) : ObservableObject where T : class
{
    /// <summary>当前数据。</summary>
    public T Model { get; private set; } = model;

    /// <summary>这一行的稳定身份(刷新时按它配对)。</summary>
    public abstract string Key { get; }

    /// <summary>换掉数据(身份不变)。</summary>
    /// <param name="model">新数据。</param>
    public void Update(T model)
    {
        if (Equals(Model, model))
        {
            return;
        }
        Model = model;
        RaisePropertyChanged(nameof(Model));
        OnModelChanged();
    }

    /// <summary>模型换过之后的钩子(派生属性通知)。</summary>
    protected virtual void OnModelChanged()
    {
    }
}

/// <summary>容器行。除了容器本身,还挂着 <c>docker stats</c> 的实时数字。</summary>
/// <param name="model">容器。</param>
public sealed class ContainerRow(ContainerItem model) : Row<ContainerItem>(model)
{
    private string _cpu = string.Empty;
    private string _memory = string.Empty;
    private string _network = string.Empty;
    private string _blockIo = string.Empty;
    private string _pids = string.Empty;

    /// <inheritdoc />
    public override string Key => Model.Id;

    /// <summary>CPU 占用。</summary>
    public string Cpu
    {
        get => _cpu;
        private set => SetProperty(ref _cpu, value);
    }

    /// <summary>内存用量。</summary>
    public string Memory
    {
        get => _memory;
        private set => SetProperty(ref _memory, value);
    }

    /// <summary>网络收发。</summary>
    public string Network
    {
        get => _network;
        private set => SetProperty(ref _network, value);
    }

    /// <summary>块设备读写。</summary>
    public string BlockIo
    {
        get => _blockIo;
        private set => SetProperty(ref _blockIo, value);
    }

    /// <summary>进程数。</summary>
    public string Pids
    {
        get => _pids;
        private set => SetProperty(ref _pids, value);
    }

    /// <summary>贴上一次统计快照;<paramref name="stats" /> 为 null 表示这个容器没在跑。</summary>
    /// <param name="stats">统计。</param>
    public void ApplyStats(StatsItem? stats)
    {
        Cpu = stats?.CpuPercent ?? string.Empty;
        Memory = stats?.MemUsage ?? string.Empty;
        Network = stats?.NetIO ?? string.Empty;
        BlockIo = stats?.BlockIO ?? string.Empty;
        Pids = stats?.Pids ?? string.Empty;
    }
}

/// <summary>镜像行。</summary>
/// <param name="model">镜像。</param>
public sealed class ImageRow(ImageItem model) : Row<ImageItem>(model)
{
    /// <inheritdoc />
    /// <remarks>同一个镜像 id 可以有多个标签,每个标签在 <c>docker images</c> 里各占一行 —— 身份必须带上标签。</remarks>
    public override string Key => $"{Model.Id}|{Model.Repository}:{Model.Tag}";
}

/// <summary>卷行。</summary>
/// <param name="model">卷。</param>
public sealed class VolumeRow(VolumeItem model) : Row<VolumeItem>(model)
{
    /// <inheritdoc />
    public override string Key => Model.Name;
}

/// <summary>网络行。</summary>
/// <param name="model">网络。</param>
public sealed class NetworkRow(NetworkItem model) : Row<NetworkItem>(model)
{
    /// <inheritdoc />
    public override string Key => Model.Id;
}

/// <summary>compose 项目行。</summary>
/// <param name="model">项目。</param>
public sealed class ComposeRow(ComposeProjectItem model) : Row<ComposeProjectItem>(model)
{
    /// <inheritdoc />
    public override string Key => Model.Name;
}

/// <summary>会话下拉里的一项。</summary>
/// <param name="SessionId">宿主的会话 id。</param>
/// <param name="Display">展示文字(<c>user@host</c>)。</param>
/// <param name="Host">主机名(设置按主机记,换个会话再连上还是同一套 sudo / DOCKER_HOST)。</param>
public sealed record SessionOption(string SessionId, string Display, string Host)
{
    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>执行记录里的一条。</summary>
/// <param name="Time">发起时刻(本地)。</param>
/// <param name="Command">远端实际执行的命令。</param>
/// <param name="ExitCode">退出码。</param>
/// <param name="Elapsed">耗时。</param>
public sealed record CommandLogEntry(DateTimeOffset Time, string Command, int ExitCode, TimeSpan Elapsed)
{
    /// <summary>一行文本形式(执行记录抽屉里就是一段纯文本)。</summary>
    public string Line =>
        $"[{Time:HH:mm:ss}] ({ExitCode}) {Elapsed.TotalMilliseconds:F0}ms  {Command}";
}

/// <summary>把新数据合进现有行集合,尽量保住行的身份(以及用户的选中态)。</summary>
public static class RowSync
{
    /// <summary>按 <see cref="Row{T}.Key" /> 就地合并。</summary>
    /// <typeparam name="TRow">行类型。</typeparam>
    /// <typeparam name="TModel">模型类型。</typeparam>
    /// <param name="target">现有集合(就地改)。</param>
    /// <param name="models">新数据(顺序即目标顺序)。</param>
    /// <param name="keyOf">从模型取身份。</param>
    /// <param name="create">造一个新行。</param>
    public static void Apply<TRow, TModel>(
        ObservableCollection<TRow> target,
        IReadOnlyList<TModel> models,
        Func<TModel, string> keyOf,
        Func<TModel, TRow> create)
        where TRow : Row<TModel>
        where TModel : class
    {
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var key = keyOf(model);
            if (i < target.Count && target[i].Key == key)
            {
                target[i].Update(model);
                continue;
            }
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (target[j].Key == key)
                {
                    existing = j;
                    break;
                }
            }
            if (existing >= 0)
            {
                var row = target[existing];
                row.Update(model);
                target.Move(existing, i);
                continue;
            }
            target.Insert(i, create(model));
        }
        while (target.Count > models.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
