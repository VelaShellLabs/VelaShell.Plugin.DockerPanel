using System.Collections.ObjectModel;
using System.Globalization;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>表单项的形态。</summary>
public enum FormFieldKind
{
    /// <summary>单行文本。</summary>
    Text,

    /// <summary>多行文本(端口/卷/环境变量这类"一行一条")。</summary>
    Multiline,

    /// <summary>勾选项。</summary>
    Boolean,

    /// <summary>下拉。</summary>
    Choice
}

/// <summary>下拉里的一项。</summary>
/// <param name="Value">回传给调用方的值。</param>
/// <param name="Label">界面上的文字。</param>
public sealed record FormChoice(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>表单里的一项。</summary>
public sealed class FormField : ObservableObject
{
    private string _value = string.Empty;
    private FormChoice? _selectedChoice;

    /// <summary>回传时的键。</summary>
    public required string Key { get; init; }

    /// <summary>标签。</summary>
    public required string Label { get; init; }

    /// <summary>形态。</summary>
    public FormFieldKind Kind { get; init; } = FormFieldKind.Text;

    /// <summary>占位提示。</summary>
    public string Placeholder { get; init; } = string.Empty;

    /// <summary>一行小字说明;为空则不占位置。</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>下拉的选项。</summary>
    public IReadOnlyList<FormChoice> Choices { get; init; } = [];

    /// <summary>多行框的高度(逻辑像素)。</summary>
    public double Height { get; init; } = 64;

    /// <summary>值(布尔项为 <c>true</c>/<c>false</c>,下拉项为选项的 Value)。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                RaisePropertyChanged(nameof(BoolValue));
                Changed?.Invoke();
            }
        }
    }

    /// <summary>勾选项绑定用的布尔视图。</summary>
    public bool BoolValue
    {
        get => bool.TryParse(Value, out var parsed) && parsed;
        set => Value = value ? "true" : "false";
    }

    /// <summary>下拉绑定用的选中项视图。</summary>
    public FormChoice? SelectedChoice
    {
        get => _selectedChoice ??= Choices.FirstOrDefault(c => c.Value == Value) ?? Choices.FirstOrDefault();
        set
        {
            if (SetProperty(ref _selectedChoice, value) && value is not null)
            {
                Value = value.Value;
            }
        }
    }

    /// <summary>单行文本形态。</summary>
    public bool IsText => Kind is FormFieldKind.Text;

    /// <summary>多行文本形态。</summary>
    public bool IsMultiline => Kind is FormFieldKind.Multiline;

    /// <summary>勾选项形态。</summary>
    public bool IsBoolean => Kind is FormFieldKind.Boolean;

    /// <summary>下拉形态。</summary>
    public bool IsChoice => Kind is FormFieldKind.Choice;

    /// <summary>有小字说明。</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>值变化(表单据此重算命令预览)。</summary>
    public Action? Changed { get; set; }
}

/// <summary>
/// 面板内的通用表单层。
/// <para>
/// 拉镜像、跑容器、建卷、建网络、重命名、打标签、接网络、进容器 —— 这八件事的界面差别
/// 只是"几个输入框 + 一个按钮"。为它们各写一个对话框是八份几乎一样的 AXAML;
/// 这里把表单**声明化**(和宿主给协议插件的连接表单是同一个思路),
/// 视图里只有一份 <c>ItemsControl</c>。
/// </para>
/// <para>
/// 表单顶上永远有一条**命令预览**:用户按下"执行"之前,就能看到那条会在生产机上跑起来的
/// 命令长什么样。这一条比表单本身更重要 —— 它把"我以为我填对了"变成"我看到了"。
/// </para>
/// </summary>
public sealed class PanelForm : ObservableObject
{
    private TaskCompletionSource<IReadOnlyDictionary<string, string>?>? _pending;
    private Func<IReadOnlyDictionary<string, string>, string>? _preview;
    private bool _isOpen;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _submitLabel = "OK";
    private string _cancelLabel = "Cancel";
    private string _previewText = string.Empty;
    private string _previewLabel = string.Empty;

    /// <summary>构造。</summary>
    public PanelForm()
    {
        SubmitCommand = new(() =>
        {
            Close(Snapshot());
            return Task.CompletedTask;
        });
        CancelCommand = new(() =>
        {
            Close(null);
            return Task.CompletedTask;
        });
    }

    /// <summary>表单是否打开。</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>标题。</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>副标题;为空则不占位置。</summary>
    public string Description
    {
        get => _description;
        private set
        {
            SetProperty(ref _description, value);
            RaisePropertyChanged(nameof(HasDescription));
        }
    }

    /// <summary>有副标题。</summary>
    public bool HasDescription => Description.Length > 0;

    /// <summary>提交按钮文案。</summary>
    public string SubmitLabel
    {
        get => _submitLabel;
        private set => SetProperty(ref _submitLabel, value);
    }

    /// <summary>取消按钮文案。</summary>
    public string CancelLabel
    {
        get => _cancelLabel;
        private set => SetProperty(ref _cancelLabel, value);
    }

    /// <summary>命令预览那一行的标签(如"将执行")。</summary>
    public string PreviewLabel
    {
        get => _previewLabel;
        private set => SetProperty(ref _previewLabel, value);
    }

    /// <summary>命令预览。</summary>
    public string PreviewText
    {
        get => _previewText;
        private set
        {
            SetProperty(ref _previewText, value);
            RaisePropertyChanged(nameof(HasPreview));
        }
    }

    /// <summary>有命令预览。</summary>
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>表单项。</summary>
    public ObservableCollection<FormField> Fields { get; } = [];

    /// <summary>提交。</summary>
    public AsyncCommand SubmitCommand { get; }

    /// <summary>取消。</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// 摆出一个表单并等结果。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="description">副标题。</param>
    /// <param name="fields">表单项。</param>
    /// <param name="submitLabel">提交按钮文案。</param>
    /// <param name="cancelLabel">取消按钮文案。</param>
    /// <param name="previewLabel">命令预览的标签;为空表示不显示预览。</param>
    /// <param name="preview">按当前值生成命令预览。</param>
    /// <returns>用户填的值;取消时为 <see langword="null" />。</returns>
    public Task<IReadOnlyDictionary<string, string>?> AskAsync(
        string title,
        string description,
        IReadOnlyList<FormField> fields,
        string submitLabel,
        string cancelLabel,
        string previewLabel = "",
        Func<IReadOnlyDictionary<string, string>, string>? preview = null)
    {
        if (_pending is not null)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        }
        Title = title;
        Description = description;
        SubmitLabel = submitLabel;
        CancelLabel = cancelLabel;
        PreviewLabel = previewLabel;
        _preview = preview;
        Fields.Clear();
        foreach (var field in fields)
        {
            field.Changed = UpdatePreview;
            Fields.Add(field);
        }
        UpdatePreview();
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        return _pending.Task;
    }

    /// <summary>关掉表单(面板释放时用,当作取消)。</summary>
    public void Dismiss() => Close(null);

    /// <summary>建一个单行文本项。</summary>
    /// <param name="key">键。</param>
    /// <param name="label">标签。</param>
    /// <param name="value">初值。</param>
    /// <param name="placeholder">占位提示。</param>
    /// <param name="hint">小字说明。</param>
    /// <returns>表单项。</returns>
    public static FormField Text(string key, string label, string value = "", string placeholder = "", string hint = "") =>
        new() { Key = key, Label = label, Kind = FormFieldKind.Text, Value = value, Placeholder = placeholder, Hint = hint };

    /// <summary>建一个多行文本项。</summary>
    /// <param name="key">键。</param>
    /// <param name="label">标签。</param>
    /// <param name="value">初值。</param>
    /// <param name="placeholder">占位提示。</param>
    /// <param name="height">高度。</param>
    /// <param name="hint">小字说明。</param>
    /// <returns>表单项。</returns>
    public static FormField Multiline(string key, string label, string value = "", string placeholder = "", double height = 64, string hint = "") =>
        new()
        {
            Key = key,
            Label = label,
            Kind = FormFieldKind.Multiline,
            Value = value,
            Placeholder = placeholder,
            Height = height,
            Hint = hint
        };

    /// <summary>建一个勾选项。</summary>
    /// <param name="key">键。</param>
    /// <param name="label">标签。</param>
    /// <param name="value">初值。</param>
    /// <param name="hint">小字说明。</param>
    /// <returns>表单项。</returns>
    public static FormField Boolean(string key, string label, bool value = false, string hint = "") =>
        new()
        {
            Key = key,
            Label = label,
            Kind = FormFieldKind.Boolean,
            Value = value ? "true" : "false",
            Hint = hint
        };

    /// <summary>建一个下拉项。</summary>
    /// <param name="key">键。</param>
    /// <param name="label">标签。</param>
    /// <param name="choices">选项。</param>
    /// <param name="value">初值。</param>
    /// <param name="hint">小字说明。</param>
    /// <returns>表单项。</returns>
    public static FormField Choice(string key, string label, IReadOnlyList<FormChoice> choices, string value = "", string hint = "") =>
        new()
        {
            Key = key,
            Label = label,
            Kind = FormFieldKind.Choice,
            Choices = choices,
            Value = value.Length > 0 ? value : choices.FirstOrDefault()?.Value ?? string.Empty,
            Hint = hint
        };

    private void UpdatePreview() =>
        PreviewText = _preview is null || PreviewLabel.Length == 0 ? string.Empty : _preview(Snapshot());

    private Dictionary<string, string> Snapshot()
    {
        Dictionary<string, string> values = [with(StringComparer.Ordinal)];
        foreach (var field in Fields)
        {
            values[field.Key] = field.Value;
        }
        return values;
    }

    private void Close(IReadOnlyDictionary<string, string>? answer)
    {
        var pending = _pending;
        _pending = null;
        _preview = null;
        IsOpen = false;
        foreach (var field in Fields)
        {
            // 断掉回调:表单项是一次性的,留着引用等于让已关闭的表单还能被旧控件唤醒。
            field.Changed = null;
        }
        pending?.TrySetResult(answer);
    }
}

/// <summary>取表单值的小工具。</summary>
public static class FormValues
{
    /// <summary>取字符串值(去首尾空白)。</summary>
    /// <param name="values">表单值。</param>
    /// <param name="key">键。</param>
    /// <param name="fallback">回退值。</param>
    /// <returns>值。</returns>
    public static string Text(this IReadOnlyDictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : fallback;

    /// <summary>取多行值(保留换行,只去首尾空白)。</summary>
    /// <param name="values">表单值。</param>
    /// <param name="key">键。</param>
    /// <returns>值。</returns>
    public static string Lines(this IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    /// <summary>取布尔值。</summary>
    /// <param name="values">表单值。</param>
    /// <param name="key">键。</param>
    /// <param name="fallback">回退值。</param>
    /// <returns>值。</returns>
    public static bool Flag(this IReadOnlyDictionary<string, string> values, string key, bool fallback = false) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>取整数值。</summary>
    /// <param name="values">表单值。</param>
    /// <param name="key">键。</param>
    /// <param name="fallback">回退值。</param>
    /// <returns>值。</returns>
    public static int Number(this IReadOnlyDictionary<string, string> values, string key, int fallback = 0) =>
        values.TryGetValue(key, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
