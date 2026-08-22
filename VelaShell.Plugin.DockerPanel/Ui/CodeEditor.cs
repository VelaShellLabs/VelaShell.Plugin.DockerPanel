using System.Reflection;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>这块文本按哪种语法着色。</summary>
public enum CodeLanguage
{
    /// <summary>不着色(纯文本)。</summary>
    None,

    /// <summary>YAML —— compose.yaml 与 <c>config</c> 展开。</summary>
    Yaml,

    /// <summary><c>.env</c>。</summary>
    DotEnv
}

/// <summary>
/// 带语法高亮的文本框。
/// <para>
/// 里面是 AvaloniaEdit 的 <see cref="TextEditor" />,但外面只露出四个属性 ——
/// compose 页有三处要它(compose.yaml、.env、config 展开),各自的差别只有
/// "什么语言、能不能改、要不要折行"。把 <c>TextEditor</c> 直接写进 AXAML 的话,
/// 这三处就得各抄一遍主题、字体、行号与配色的接线。
/// </para>
/// <para>
/// 为什么不是 <c>TextBox</c>:<c>TextBox</c> 的整块文本共用一个 <c>Foreground</c>,
/// 按 token 着色它给不了。而 compose.yaml 恰恰是"缩进错一格就全错"的格式,
/// 高亮在这里不是装饰,是校对工具。
/// </para>
/// </summary>
public sealed class CodeEditor : UserControl
{
    private static readonly Lock RegistrationGate = new();
    private static bool _registered;

    private readonly TextEditor _editor;
    private bool _syncing;

    /// <summary>文本。双向 —— 编辑器里改了要回到视图模型。</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>按哪种语法着色。</summary>
    public static readonly StyledProperty<CodeLanguage> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, CodeLanguage>(nameof(Language));

    /// <summary>只读(<c>config</c> 展开是 compose 算出来的,改它没有意义)。</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(IsReadOnly));

    /// <summary>自动折行。</summary>
    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(WordWrap));

    /// <summary>显示行号。</summary>
    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(ShowLineNumbers), true);

    /// <summary>建编辑器。</summary>
    public CodeEditor()
    {
        _editor = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new(0),
            Padding = new(10, 8),
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _editor.Options.IndentationSize = 2;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.HighlightCurrentLine = true;
        _editor.TextChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }
            _syncing = true;
            SetCurrentValue(TextProperty, _editor.Text);
            _syncing = false;
        };
        Content = _editor;
        Focusable = false;
        ActualThemeVariantChanged += (_, _) => ApplyLanguage();
    }

    /// <inheritdoc cref="TextProperty" />
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="LanguageProperty" />
    public CodeLanguage Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    /// <inheritdoc cref="IsReadOnlyProperty" />
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <inheritdoc cref="WordWrapProperty" />
    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    /// <inheritdoc cref="ShowLineNumbersProperty" />
    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing)
        {
            _syncing = true;
            string incoming = Text ?? "";
            // 只在真不一样时才写:同一份文本写回去会把光标顶到开头,
            // 用户打一个字就跳一次。
            if (_editor.Text != incoming)
            {
                _editor.Text = incoming;
            }
            _syncing = false;
        }
        else if (change.Property == LanguageProperty || change.Property == ForegroundProperty ||
                 change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            ApplyLanguage();
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            _editor.IsReadOnly = IsReadOnly;
        }
        else if (change.Property == WordWrapProperty)
        {
            _editor.WordWrap = WordWrap;
        }
        else if (change.Property == ShowLineNumbersProperty)
        {
            _editor.ShowLineNumbers = ShowLineNumbers;
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyLanguage();
    }

    /// <summary>把语言、字体与当前主题的配色一起落到编辑器上。</summary>
    private void ApplyLanguage()
    {
        _editor.FontFamily = FontFamily;
        _editor.FontSize = FontSize;
        if (Foreground is { } foreground)
        {
            _editor.Foreground = foreground;
        }
        if (Resource("VelaAccent") is IBrush caret)
        {
            _editor.TextArea.Caret.CaretBrush = caret;
        }
        if (Resource("VelaAccentDim") is ISolidColorBrush selection)
        {
            _editor.TextArea.SelectionBrush = selection;
            _editor.TextArea.SelectionBorder = null;
        }
        if (Resource("VelaTextTertiary") is IBrush lineNumbers)
        {
            _editor.LineNumbersForeground = lineNumbers;
        }
        _editor.SyntaxHighlighting = Definition(Language) is { } definition ? Recolor(definition) : null;
    }

    /// <summary>
    /// 按当前主题给定义重着色。
    /// <para>
    /// xshd 里写死的是 Dracula 的暗色值;亮色主题下那几个浅黄浅绿压在奶白底上等于隐形。
    /// 所以颜色的**唯一事实来源**是主题字典里的 DockerCode* 令牌,xshd 只负责"哪一段算什么角色"。
    /// </para>
    /// <para>
    /// 定义是 <see cref="HighlightingManager" /> 里的全局单例,重着色改的是同一个对象 ——
    /// 这正是想要的:主题一切,下次渲染就跟着变。
    /// </para>
    /// </summary>
    private IHighlightingDefinition Recolor(IHighlightingDefinition definition)
    {
        foreach (HighlightingColor color in definition.NamedHighlightingColors)
        {
            if (Role(color.Name) is { } token && Resource(token) is ISolidColorBrush brush)
            {
                color.Foreground = new SimpleHighlightingBrush(brush.Color);
            }
        }
        return definition;
    }

    private static string? Role(string name) => name switch
    {
        "Comment" => "DockerCodeComment",
        "String" => "DockerCodeString",
        "Number" or "Constant" => "DockerCodeNumber",
        "Key" => "DockerCodeKey",
        "Section" => "DockerCodeSection",
        "Punctuation" => "DockerCodePunctuation",
        "Variable" => "VelaWarning",
        _ => null
    };

    private object? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) ? value : null;

    /// <summary>
    /// 取一份语法定义。
    /// <para>
    /// 定义随插件自带(<c>Syntax/*.xshd</c> 的嵌入资源),注册名带 <c>Docker.</c> 前缀:
    /// <see cref="HighlightingManager.Instance" /> 是**整个进程共享**的,宿主自己也往里注册
    /// "YAML"、"Ini" —— 不加前缀就会互相顶掉,而且谁先跑起来是不确定的。
    /// </para>
    /// </summary>
    private static IHighlightingDefinition? Definition(CodeLanguage language)
    {
        if (language == CodeLanguage.None)
        {
            return null;
        }
        EnsureRegistered();
        return HighlightingManager.Instance.GetDefinition(language == CodeLanguage.Yaml
            ? "Docker.Yaml"
            : "Docker.DotEnv");
    }

    private static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered)
            {
                return;
            }
            _registered = true;
            Assembly assembly = typeof(CodeEditor).Assembly;
            foreach ((string resource, string[] extensions) in
                     new[]
                     {
                         ("VelaShell.Plugin.DockerPanel.Syntax.Yaml.xshd", new[] { ".yaml", ".yml" }),
                         ("VelaShell.Plugin.DockerPanel.Syntax.DotEnv.xshd", new[] { ".env" })
                     })
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null)
                    {
                        continue;
                    }
                    using XmlReader reader = XmlReader.Create(stream);
                    IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    HighlightingManager.Instance.RegisterHighlighting(definition.Name, extensions, definition);
                }
                catch (Exception)
                {
                    // 一份定义坏掉不该让编辑器打不开 —— 那一种退化成纯文本,其余照常。
                }
            }
        }
    }
}
