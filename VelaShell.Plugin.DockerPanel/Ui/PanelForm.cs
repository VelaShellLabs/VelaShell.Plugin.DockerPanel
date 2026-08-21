using System.Collections.ObjectModel;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>表单里的一个字段。</summary>
public abstract class FormField(string label) : ObservableObject
{
    private string? _error;

    /// <summary>标签。</summary>
    public string Label { get; } = label;

    /// <summary>标签右侧的灰色提示。</summary>
    public string? Hint { get; init; }

    /// <summary>字段下方的校验错误;为空表示没问题。</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有没有校验错误。</summary>
    public bool HasError => !string.IsNullOrEmpty(_error);
}

/// <summary>一行文本。</summary>
public sealed class TextField(string label) : FormField(label)
{
    private string _value = "";

    /// <summary>值。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                Error = null;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>占位文字。</summary>
    public string Placeholder { get; init; } = "";

    /// <summary>是不是等宽(路径、id、命令用等宽)。</summary>
    public bool Mono { get; init; } = true;

    /// <summary>只读(源镜像、当前名称这类"给你看清楚"的字段)。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>值变了。</summary>
    public event Action? Changed;
}

/// <summary>一个开关。</summary>
public sealed class ToggleField(string label) : FormField(label)
{
    private bool _value;

    /// <summary>值。</summary>
    public bool Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>开关下面那行说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>打开这一项是危险的(特权模式之类)。</summary>
    public bool Danger { get; init; }

    /// <summary>值变了。</summary>
    public event Action? Changed;
}

/// <summary>下拉/分段里的一个选项。</summary>
/// <param name="Value">值。</param>
/// <param name="Label">显示文字。</param>
/// <param name="Description">补充说明(单选列表才显示)。</param>
/// <param name="Enabled">能不能选。</param>
/// <param name="DisabledReason">不能选的原因。</param>
public sealed record ChoiceOption(string Value, string Label, string Description = "", bool Enabled = true,
    string DisabledReason = "");

/// <summary>一组互斥选项(分段控件或下拉)。</summary>
public sealed class ChoiceField : FormField
{
    private string _value = "";

    /// <summary>建一组互斥选项。</summary>
    public ChoiceField(string label) : base(label) =>
        SelectCommand = new(p =>
        {
            if (p is ChoiceOption { Enabled: true } option)
            {
                Value = option.Value;
            }
        });

    /// <summary>选项。</summary>
    public ObservableCollection<ChoiceOption> Options { get; } = [];

    /// <summary>当前值。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                OnPropertyChanged(nameof(SelectedLabel));
                Changed?.Invoke();
            }
        }
    }

    /// <summary>当前值对应的显示文字。</summary>
    public string SelectedLabel => Options.FirstOrDefault(o => o.Value == _value)?.Label ?? _value;

    /// <summary>用分段控件呈现(选项少时)还是下拉(选项多时)。</summary>
    public bool AsSegments { get; init; }

    /// <summary>选中变了。</summary>
    public event Action? Changed;

    /// <summary>选一个。</summary>
    public RelayCommand SelectCommand { get; }
}

/// <summary>带说明的单选列表(重启策略那种)。</summary>
public sealed class RadioListField : FormField
{
    private string _value = "";

    /// <summary>建一个单选列表。</summary>
    public RadioListField(string label) : base(label) =>
        SelectCommand = new(p =>
        {
            if (p is ChoiceOption { Enabled: true } option)
            {
                Value = option.Value;
            }
        });

    /// <summary>选项。</summary>
    public ObservableCollection<ChoiceOption> Options { get; } = [];

    /// <summary>当前值。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                OnPropertyChanged(nameof(SelectedValue));
            }
        }
    }

    /// <summary>当前值(绑定用的别名,方便模板里做相等判断)。</summary>
    public string SelectedValue => _value;

    /// <summary>选一个。</summary>
    public RelayCommand SelectCommand { get; }
}

/// <summary>键值行(端口、卷、环境变量、驱动选项共用)。</summary>
public sealed class PairRow(string key, string value) : ObservableObject
{
    private string _key = key;
    private string _value = value;

    /// <summary>左侧。</summary>
    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    /// <summary>右侧。</summary>
    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    /// <summary>第三格(协议、只读标记);不用时为空。</summary>
    public string Extra { get; set; } = "";
}

/// <summary>可增删的键值列表。</summary>
public sealed class PairListField : FormField
{
    /// <summary>建一个键值列表。</summary>
    public PairListField(string label) : base(label)
    {
        AddCommand = new(_ => Rows.Add(new("", "")));
        RemoveCommand = new(p =>
        {
            if (p is PairRow row)
            {
                Rows.Remove(row);
            }
        });
        // 增删行、以及行里任一格的改动,都要把"等效命令"重算一遍 ——
        // 那条预览存在的全部意义就是让用户核对自己填的东西,它一旦滞后就成了误导。
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (PairRow added in e.NewItems?.OfType<PairRow>() ?? [])
            {
                added.PropertyChanged += OnRowChanged;
            }
            foreach (PairRow removed in e.OldItems?.OfType<PairRow>() ?? [])
            {
                removed.PropertyChanged -= OnRowChanged;
            }
            Changed?.Invoke();
        };
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Changed?.Invoke();

    /// <summary>任一行增删或改动。</summary>
    public event Action? Changed;

    /// <summary>行。</summary>
    public ObservableCollection<PairRow> Rows { get; } = [];

    /// <summary>左侧占位。</summary>
    public string KeyPlaceholder { get; init; } = "";

    /// <summary>右侧占位。</summary>
    public string ValuePlaceholder { get; init; } = "";

    /// <summary>两格之间的符号(“→” / “=” / “:”)。</summary>
    public string Separator { get; init; } = "=";

    /// <summary>“+ 添加”的文字。</summary>
    public string AddLabel { get; init; } = "+ 添加";

    /// <summary>添加一行。</summary>
    public RelayCommand AddCommand { get; }

    /// <summary>删掉一行。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>非空行(两格都填了才算)。</summary>
    public IEnumerable<PairRow> Filled =>
        Rows.Where(r => r.Key.Trim().Length > 0 && r.Value.Trim().Length > 0);
}

/// <summary>可多选列表里的一项。</summary>
public sealed class SelectItem(string id, string label, string meta, bool enabled, string disabledReason = "")
    : ObservableObject
{
    private bool _selected;

    /// <summary>标识。</summary>
    public string Id { get; } = id;

    /// <summary>显示文字。</summary>
    public string Label { get; } = label;

    /// <summary>右侧小字。</summary>
    public string Meta { get; } = meta;

    /// <summary>能不能选。</summary>
    public bool Enabled { get; } = enabled;

    /// <summary>不能选的原因。</summary>
    public string DisabledReason { get; } = disabledReason;

    /// <summary>选中了没有。</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (Enabled)
            {
                SetField(ref _selected, value);
            }
        }
    }
}

/// <summary>带搜索的多选列表。</summary>
public sealed class SelectListField(string label) : FormField(label)
{
    private string _search = "";

    /// <summary>全部项。</summary>
    public ObservableCollection<SelectItem> Items { get; } = [];

    /// <summary>过滤后的项。</summary>
    public ObservableCollection<SelectItem> View { get; } = [];

    /// <summary>搜索词。</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>搜索框占位。</summary>
    public string Placeholder { get; init; } = "过滤…";

    /// <summary>已选的项。</summary>
    public IEnumerable<SelectItem> SelectedItems => Items.Where(i => i.Selected);

    /// <summary>重建过滤视图。</summary>
    public void ApplyFilter()
    {
        View.Clear();
        foreach (SelectItem item in Items.Where(i =>
                     _search.Length == 0 || i.Label.Contains(_search, StringComparison.OrdinalIgnoreCase)))
        {
            View.Add(item);
        }
    }
}

/// <summary>
/// 一个表单弹窗。
/// <para>
/// 全部复用同一个壳:标题栏 → 主机条 → 内容 → 页脚。主机条一行都不能省 ——
/// 同时开着两台机器时,它是最便宜的保险。
/// </para>
/// </summary>
public abstract class PanelForm : ObservableObject
{
    private string _commandPreview = "";
    private string _commandNote = "";
    private string? _formError;

    /// <summary>标题。</summary>
    public abstract string Title { get; }

    /// <summary>标题图标。</summary>
    public virtual string Icon => "Icon.settings";

    /// <summary>确认按钮文字。</summary>
    public abstract string ConfirmLabel { get; }

    /// <summary>确认按钮图标。</summary>
    public virtual string ConfirmIcon => "Docker.check";

    /// <summary>页脚左侧的提示。</summary>
    public virtual string FooterHint => "Esc 取消";

    /// <summary>字段。</summary>
    public ObservableCollection<FormField> Fields { get; } = [];

    /// <summary>“等效命令”里显示的那条请求。</summary>
    public string CommandPreview
    {
        get => _commandPreview;
        protected set => SetField(ref _commandPreview, value);
    }

    /// <summary>请求下面那行等价的命令行。</summary>
    public string CommandNote
    {
        get => _commandNote;
        protected set => SetField(ref _commandNote, value);
    }

    /// <summary>有没有命令预览。</summary>
    public bool HasPreview => CommandPreview.Length > 0;

    /// <summary>整表级别的错误。</summary>
    public string? FormError
    {
        get => _formError;
        set
        {
            if (SetField(ref _formError, value))
            {
                OnPropertyChanged(nameof(HasFormError));
            }
        }
    }

    /// <summary>有没有整表错误。</summary>
    public bool HasFormError => !string.IsNullOrEmpty(_formError);

    /// <summary>
    /// 校验。返回 <see langword="false" /> 时把原因写在字段的 <see cref="FormField.Error" />
    /// 或 <see cref="FormError" /> 上 —— 让用户知道**哪一格**不对,而不是一句"输入有误"。
    /// </summary>
    public virtual bool Validate() => true;

    /// <summary>字段变动后重算命令预览。</summary>
    protected virtual void UpdatePreview()
    {
    }

    /// <summary>把某个字段的变动接到预览上。</summary>
    protected void Watch(TextField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }

    /// <summary>把某个开关接到预览上。</summary>
    protected void Watch(ToggleField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }

    /// <summary>把某个选择接到预览上。</summary>
    protected void Watch(ChoiceField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }

    /// <summary>把某个键值列表接到预览上。</summary>
    protected void Watch(PairListField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }
}
