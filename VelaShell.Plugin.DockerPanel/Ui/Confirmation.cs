namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>确认框的答案。</summary>
/// <param name="Confirmed">用户是否确认。</param>
/// <param name="Option">附加勾选项的最终状态(没有勾选项时为 false)。</param>
public readonly record struct ConfirmAnswer(bool Confirmed, bool Option);

/// <summary>
/// 面板内的确认闸门。
/// <para>
/// **为什么自己画而不是弹系统对话框**:SDK 的界面能力只给"开一个面板",没有"弹一个模态框";
/// 而更实在的理由是 —— 确认必须贴在**出事的那个面板**上。同时开着生产与测试两台机器的
/// Docker 面板时,一个飘在屏幕中央、不写清是哪台主机的"确定要删除 3 个卷吗",
/// 是这个插件能犯的最贵的错误。所以确认框长在面板里,标题里带着主机名。
/// </para>
/// <para>
/// 两档护栏:一般的破坏性操作(删容器、删镜像)给一句后果说明 + 危险色确认按钮;
/// **会丢数据**的那些(删卷、<c>system prune --volumes</c>、覆盖远端 compose 文件)
/// 额外要求手打确认串 —— 与删仓库同款,因为后果同款。
/// </para>
/// </summary>
public sealed class Confirmation : ObservableObject
{
    private TaskCompletionSource<ConfirmAnswer>? _pending;
    private bool _isOpen;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _detail = string.Empty;
    private string _confirmLabel = "OK";
    private string _cancelLabel = "Cancel";
    private string _optionLabel = string.Empty;
    private bool _optionValue;
    private bool _isDestructive;
    private string _expectedText = string.Empty;
    private string _typedText = string.Empty;
    private string _typePrompt = string.Empty;

    /// <summary>构造。</summary>
    public Confirmation()
    {
        ConfirmCommand = new(() =>
        {
            Close(new(true, OptionValue));
            return Task.CompletedTask;
        }, () => CanConfirm);
        CancelCommand = new(() =>
        {
            Close(new(false, false));
            return Task.CompletedTask;
        });
    }

    /// <summary>确认框是否打开。</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>标题(一句话说清要做什么,带主机名)。</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>后果说明(为什么值得停一下)。</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>将要执行的命令,等宽显示 —— 让用户看到**确切**的东西,而不是一句概括。</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>确认按钮文案。</summary>
    public string ConfirmLabel
    {
        get => _confirmLabel;
        private set => SetProperty(ref _confirmLabel, value);
    }

    /// <summary>取消按钮文案。</summary>
    public string CancelLabel
    {
        get => _cancelLabel;
        private set => SetProperty(ref _cancelLabel, value);
    }

    /// <summary>附加勾选项的文案(如"连匿名卷一起删");为空表示没有勾选项。</summary>
    public string OptionLabel
    {
        get => _optionLabel;
        private set
        {
            SetProperty(ref _optionLabel, value);
            RaisePropertyChanged(nameof(HasOption));
        }
    }

    /// <summary>是否有附加勾选项。</summary>
    public bool HasOption => OptionLabel.Length > 0;

    /// <summary>附加勾选项的状态。</summary>
    public bool OptionValue
    {
        get => _optionValue;
        set => SetProperty(ref _optionValue, value);
    }

    /// <summary>是否为"会丢数据"档(界面据此把确认按钮染成危险色)。</summary>
    public bool IsDestructive
    {
        get => _isDestructive;
        private set => SetProperty(ref _isDestructive, value);
    }

    /// <summary>要求键入的串;为空表示不要求。</summary>
    public string ExpectedText
    {
        get => _expectedText;
        private set
        {
            SetProperty(ref _expectedText, value);
            RaisePropertyChanged(nameof(RequiresTyping));
        }
    }

    /// <summary>"键入 xxx 以确认"这句提示。</summary>
    public string TypePrompt
    {
        get => _typePrompt;
        private set => SetProperty(ref _typePrompt, value);
    }

    /// <summary>是否要求手打确认串。</summary>
    public bool RequiresTyping => ExpectedText.Length > 0;

    /// <summary>用户键入的串。</summary>
    public string TypedText
    {
        get => _typedText;
        set
        {
            SetProperty(ref _typedText, value);
            RaisePropertyChanged(nameof(CanConfirm));
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>确认按钮是否可用。</summary>
    public bool CanConfirm => !RequiresTyping || string.Equals(TypedText.Trim(), ExpectedText, StringComparison.Ordinal);

    /// <summary>确认。</summary>
    public AsyncCommand ConfirmCommand { get; }

    /// <summary>取消。</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// 问一次并等答案。
    /// <para>
    /// 同时只允许一个确认在飞:第二个请求直接被拒(当作取消)。排队会让用户在第一个框上
    /// 点完"确认"之后,莫名其妙地被问第二个他早已忘了的问题 —— 而这里的每个问题都关乎删东西。
    /// </para>
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="message">后果说明。</param>
    /// <param name="detail">将要执行的命令。</param>
    /// <param name="confirmLabel">确认按钮文案。</param>
    /// <param name="cancelLabel">取消按钮文案。</param>
    /// <param name="destructive">是否危险档。</param>
    /// <param name="expectedText">要求键入的串;为空表示不要求。</param>
    /// <param name="typePrompt">"键入 xxx 以确认"提示。</param>
    /// <param name="optionLabel">附加勾选项文案;为空表示没有。</param>
    /// <param name="optionDefault">附加勾选项的初值。</param>
    /// <returns>用户的答案。</returns>
    public Task<ConfirmAnswer> AskAsync(
        string title,
        string message,
        string detail,
        string confirmLabel,
        string cancelLabel,
        bool destructive = false,
        string? expectedText = null,
        string? typePrompt = null,
        string? optionLabel = null,
        bool optionDefault = false)
    {
        if (_pending is not null)
        {
            return Task.FromResult(new ConfirmAnswer(false, false));
        }
        Title = title;
        Message = message;
        Detail = detail;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        IsDestructive = destructive;
        ExpectedText = expectedText ?? string.Empty;
        TypePrompt = typePrompt ?? string.Empty;
        OptionLabel = optionLabel ?? string.Empty;
        OptionValue = optionDefault;
        TypedText = string.Empty;
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        RaisePropertyChanged(nameof(CanConfirm));
        ConfirmCommand.RaiseCanExecuteChanged();
        return _pending.Task;
    }

    /// <summary>关掉确认框(面板释放时用,当作取消)。</summary>
    public void Dismiss() => Close(new(false, false));

    private void Close(ConfirmAnswer answer)
    {
        var pending = _pending;
        _pending = null;
        IsOpen = false;
        pending?.TrySetResult(answer);
    }
}
