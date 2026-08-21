using System.Collections.ObjectModel;
using Avalonia.Threading;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>反馈的语气。</summary>
public enum FeedbackKind
{
    /// <summary>中性。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>部分成功 / 需要注意。</summary>
    Warning,

    /// <summary>失败。</summary>
    Error
}

/// <summary>面板右下角的一条 toast。</summary>
public sealed class Toast : ObservableObject
{
    /// <summary>建一条 toast。</summary>
    public Toast(FeedbackKind kind, string title, string detail)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
    }

    /// <summary>语气。</summary>
    public FeedbackKind Kind { get; }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>正文。</summary>
    public string Detail { get; }

    /// <summary>可点的动作(文字 → 回调)。</summary>
    public ObservableCollection<ToastAction> Actions { get; } = [];

    /// <summary>图标资源键。</summary>
    public string Icon => Kind switch
    {
        FeedbackKind.Success => "Docker.circle-check-big",
        FeedbackKind.Warning => "Icon.triangle-alert",
        FeedbackKind.Error => "Docker.circle-x",
        _ => "Icon.info"
    };

    /// <summary>是否自动消失。成功的 4 秒后自己走,失败的**不自动消失**。</summary>
    public bool AutoDismiss => Kind is FeedbackKind.Success or FeedbackKind.Info;
}

/// <summary>toast 上的一个动作。</summary>
/// <param name="Label">文字。</param>
/// <param name="Invoke">点击回调。</param>
public sealed record ToastAction(string Label, Action Invoke);

/// <summary>
/// 结果反馈。
/// <para>
/// 规则只有一条:<b>动作发起点还在视野里,就只更新状态栏和那一行;用户已经切走了
/// (换了页签、关了对话框),才弹 toast。</b> 在用户正盯着的列表上方再弹一个
/// "已停止 nginx-proxy",只是把他刚看到的事情又说了一遍。
/// </para>
/// </summary>
public sealed class Feedback : ObservableObject
{
    private const int MaxToasts = 3;
    private string _statusText = "";
    private FeedbackKind _statusKind = FeedbackKind.Info;

    /// <summary>当前的 toast(最多三条,新的在下面)。</summary>
    public ObservableCollection<Toast> Toasts { get; } = [];

    /// <summary>状态栏左侧那句话 —— 永远说最后一件事的结果。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>状态栏那句话的语气。</summary>
    public FeedbackKind StatusKind
    {
        get => _statusKind;
        private set
        {
            if (SetField(ref _statusKind, value))
            {
                OnPropertyChanged(nameof(StatusIcon));
            }
        }
    }

    /// <summary>状态栏图标。</summary>
    public string StatusIcon => _statusKind switch
    {
        FeedbackKind.Success => "Docker.circle-check-big",
        FeedbackKind.Warning => "Icon.triangle-alert",
        FeedbackKind.Error => "Docker.circle-x",
        _ => "Icon.info"
    };

    /// <summary>只更新状态栏。</summary>
    public void Status(FeedbackKind kind, string text) => Ui.Post(() =>
    {
        StatusKind = kind;
        StatusText = text;
    });

    /// <summary>弹一条 toast,同时更新状态栏。</summary>
    public Toast Notify(FeedbackKind kind, string title, string detail, params ToastAction[] actions)
    {
        var toast = new Toast(kind, title, detail);
        foreach (ToastAction action in actions)
        {
            toast.Actions.Add(action);
        }
        Ui.Post(() =>
        {
            StatusKind = kind;
            StatusText = detail.Length > 0 ? $"{title} · {detail.Split('\n')[0]}" : title;
            Toasts.Add(toast);
            while (Toasts.Count > MaxToasts)
            {
                Toasts.RemoveAt(0);
            }
            if (toast.AutoDismiss)
            {
                DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(4));
            }
        });
        return toast;
    }

    /// <summary>关掉一条 toast。</summary>
    public void Dismiss(Toast toast) => Ui.Post(() => Toasts.Remove(toast));

    /// <summary>
    /// 把一次批量操作的结果如实报出来。成功全过就一句话;有失败的话**逐个目标**列原因,
    /// 而不是把整批说成"操作失败"。
    /// </summary>
    /// <param name="verb">动作名,如“停止”。</param>
    /// <param name="result">批量结果。</param>
    /// <param name="inView">发起点是否还在用户视野里(决定要不要弹 toast)。</param>
    /// <param name="onShowDetail">用户点"查看详情"时的回调。</param>
    public void ReportBatch(string verb, BatchResult result, bool inView, Action? onShowDetail = null)
    {
        if (result.AllSucceeded)
        {
            string text = $"已{verb} {result.SucceededCount} 个";
            if (inView)
            {
                Status(FeedbackKind.Success, text);
            }
            else
            {
                Notify(FeedbackKind.Success, text, "");
            }
            return;
        }
        string summary = $"已{verb} {result.SucceededCount} 个,{result.FailedCount} 个失败";
        string detail = string.Join('\n', result.Failures.Take(3).Select(f => $"{f.Target}:{f.Failure}"));
        if (result.FailedCount > 3)
        {
            detail += $"\n…还有 {result.FailedCount - 3} 个";
        }
        Status(FeedbackKind.Warning, $"{summary} · {result.Failures.First().Target}:{result.Failures.First().Failure}");
        List<ToastAction> actions = [];
        if (onShowDetail is not null)
        {
            actions.Add(new("查看详情", onShowDetail));
        }
        Notify(FeedbackKind.Warning, summary, detail, [.. actions]);
    }

    /// <summary>把一个异常如实报出来。连不上与操作失败的语气不一样。</summary>
    public void ReportError(string what, Exception ex)
    {
        string detail = ex switch
        {
            DockerApiException api => api.Message,
            DockerUnreachableException unreachable => unreachable.Message,
            OperationCanceledException => "已取消。",
            _ => ex.Message
        };
        if (ex is OperationCanceledException)
        {
            Status(FeedbackKind.Info, $"{what} 已取消");
            return;
        }
        Notify(FeedbackKind.Error, $"{what} 失败", detail);
    }
}
