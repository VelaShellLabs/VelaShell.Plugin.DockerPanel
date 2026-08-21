using Avalonia.Threading;
using System.Text;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    /// <summary>事件触发刷新的最小间隔。`compose up` 一次能推来几十条事件,一条一刷是自找的卡顿。</summary>
    private static readonly TimeSpan EventCoalesceWindow = TimeSpan.FromMilliseconds(700);

    /// <summary>日志缓冲的上限(字符)。跟随一整天的容器不该把面板吃成一个内存黑洞。</summary>
    private const int LogBufferLimit = 512 * 1024;

    private readonly StringBuilder _logBuffer = new();
    private readonly Lock _logGate = new();

    private CancellationTokenSource? _eventsCts;
    private CancellationTokenSource? _logCts;
    private bool _logFlushPending;
    private bool _isLive;
    private long _lastEventRefreshTicks;
    private bool _eventRefreshQueued;

    /// <summary>
    /// 事件流接上了 —— 面板此刻是"活的":别处起了个容器,这里一秒内自己就更新。
    /// </summary>
    public bool IsLive
    {
        get => _isLive;
        private set => SetProperty(ref _isLive, value);
    }

    /// <summary>
    /// 接上 daemon 的事件流。
    /// <para>
    /// 这是 §9"能用事件就不用定时器"的兑现:接上之后自动刷新可以整个关掉,
    /// 而界面反而更快 —— 容器起停、镜像拉取、卷创建都在事件里。
    /// 接不上(老 daemon、权限受限)就退回定时刷新,面板不会因此少一个功能。
    /// </para>
    /// </summary>
    private void StartEventStream()
    {
        StopEventStream();
        if (_api is not { } api)
        {
            return;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _eventsCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await api.StreamEventsAsync(OnDockerEvent, cts.Token).ConfigureAwait(false);
                // 正常结束(daemon 重启、通道关闭)也算掉线。
                Dispatcher.UIThread.Post(() => FallBackToTimedRefresh(cts));
            }
            catch (OperationCanceledException)
            {
                // 换会话 / 关面板的正常收尾。
            }
            catch (Exception ex)
            {
                _context.Log.Info($"Docker event stream unavailable, falling back to timed refresh: {ex.Message}");
                Dispatcher.UIThread.Post(() => FallBackToTimedRefresh(cts));
            }
        }, CancellationToken.None);
        IsLive = true;
    }

    /// <summary>
    /// 事件流断了(或压根接不上)时的退路。
    /// <para>
    /// 面板默认不开定时刷新 —— 因为事件流在管。事件流没了却还不开定时器,用户看到的就是
    /// 一块**静止的、看起来正常的**面板,那比明说"没连上"更糟。所以这里自动补上一档
    /// 30 秒的定时刷新;用户当然可以再改。
    /// </para>
    /// </summary>
    /// <param name="cts">发起这条流时用的令牌源(用来确认不是被新的流顶掉了)。</param>
    private void FallBackToTimedRefresh(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_eventsCts, cts))
        {
            return;
        }
        IsLive = false;
        if (AutoRefreshSeconds <= 0 && IsEngineReady)
        {
            AutoRefreshSeconds = 30;
            RaisePropertyChanged(nameof(SelectedRefreshChoice));
        }
    }

    private void StopEventStream()
    {
        var cts = _eventsCts;
        _eventsCts = null;
        IsLive = false;
        Cancel(cts);
    }

    /// <summary>事件到达(I/O 线程)。这里只做一件事:把"该刷新了"记下来。</summary>
    /// <param name="dockerEvent">事件。</param>
    private void OnDockerEvent(DockerEvent dockerEvent)
    {
        if (!dockerEvent.AffectsLists)
        {
            return;
        }
        // 合并:`compose up` 一次推来几十条,逐条刷新会把远端敲成筛子。
        // 窗口内已经排了一次就不再排,窗口外立刻排一次。
        var now = Environment.TickCount64;
        lock (_logGate)
        {
            if (_eventRefreshQueued)
            {
                return;
            }
            _eventRefreshQueued = true;
        }
        var wait = Math.Max(0, EventCoalesceWindow.Ticks / TimeSpan.TicksPerMillisecond - (now - _lastEventRefreshTicks));
        _ = Task.Run(async () =>
        {
            try
            {
                if (wait > 0)
                {
                    await Task.Delay((int)wait, _lifetime.Token).ConfigureAwait(false);
                }
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    lock (_logGate)
                    {
                        _eventRefreshQueued = false;
                    }
                    _lastEventRefreshTicks = Environment.TickCount64;
                    // 用户正对着确认框或表单时不刷新:列表在脚下变掉会把"我要删的是这一行"变成谎话。
                    if (!Confirm.IsOpen && !Form.IsOpen && IsEngineReady)
                    {
                        await RefreshActiveAsync(true).ConfigureAwait(true);
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停机路径。
            }
            catch (Exception ex)
            {
                _context.Log.Warn("Event-driven refresh failed.", ex);
            }
        }, CancellationToken.None);
    }

    /// <summary>开始跟随选中容器的日志。</summary>
    private void StartLogStream()
    {
        StopLogStream();
        if (_api is not { } api || PrimaryContainer is not { } container)
        {
            return;
        }
        lock (_logGate)
        {
            _logBuffer.Clear();
        }
        DrawerText = string.Empty;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _logCts = cts;
        var id = container.Model.Id;
        var tail = LogTail;
        var timestamps = LogTimestamps;
        _ = Task.Run(async () =>
        {
            try
            {
                await api.StreamLogsAsync(id, tail, timestamps, AppendLogLine, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 关掉跟随 / 换容器 / 关面板的正常收尾。
            }
            catch (Exception ex)
            {
                AppendLogLine($"[stream ended: {ex.Message}]");
            }
        }, CancellationToken.None);
    }

    private void StopLogStream()
    {
        var cts = _logCts;
        _logCts = null;
        Cancel(cts);
    }

    /// <summary>
    /// 收到一行日志(I/O 线程)。
    /// <para>
    /// 攒批而不是每行推一次界面:一个刷屏的容器一秒能吐几千行,每行一次绑定更新会把
    /// UI 线程钉死。这里往缓冲里追加,并且**最多排一次**待处理的刷新 —— 那一次刷新
    /// 会把期间攒下的全部内容一起交出去。
    /// </para>
    /// </summary>
    /// <param name="line">一行日志。</param>
    private void AppendLogLine(string line)
    {
        bool schedule;
        lock (_logGate)
        {
            _logBuffer.Append(line).Append('\n');
            if (_logBuffer.Length > LogBufferLimit)
            {
                // 从中间截,并且**按行**截:半行开头的日志比少几行更难读。
                var cut = _logBuffer.Length - (LogBufferLimit * 3 / 4);
                var kept = _logBuffer.ToString(cut, _logBuffer.Length - cut);
                var firstBreak = kept.IndexOf('\n');
                _logBuffer.Clear();
                _logBuffer.Append(firstBreak >= 0 ? kept[(firstBreak + 1)..] : kept);
            }
            schedule = !_logFlushPending;
            _logFlushPending = true;
        }
        if (schedule)
        {
            Dispatcher.UIThread.Post(FlushLog, DispatcherPriority.Background);
        }
    }

    private void FlushLog()
    {
        string text;
        lock (_logGate)
        {
            _logFlushPending = false;
            text = _logBuffer.ToString();
        }
        if (DrawerContent is DrawerTab.Logs)
        {
            _logRaw = text;
            DrawerText = FilterLog(text);
        }
    }

    /// <summary>取消一个流并释放它的令牌源(取消本身可能已在停机路径上发生过)。</summary>
    /// <param name="cts">令牌源。</param>
    private static void Cancel(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经收过了。
        }
        cts.Dispose();
    }
}
