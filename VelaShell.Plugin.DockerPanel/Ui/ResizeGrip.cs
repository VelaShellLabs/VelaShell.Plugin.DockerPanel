using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 一条竖着的拖拽手柄:按住左右拖,把绑上来的 <see cref="Value" /> 改成拖出来的宽度。
/// <para>
/// 不用 <c>GridSplitter</c>:那家伙改的是 <c>ColumnDefinition.Width</c> 本身,
/// 而这个面板的列宽是**列头和数据行共用的一份视图模型状态** ——
/// 让手柄直接改那份状态,列头和几百行数据行就自然一起动;
/// 让 GridSplitter 去改列定义,则只有它所在的那个 Grid 会动。
/// </para>
/// <para>
/// 位移按**视觉根**算,不按自己算:拖动会当场改变自己的位置,
/// 以自己为参照系就会形成"动一下 → 参照系跟着动 → 再算一次"的抖动。
/// </para>
/// </summary>
public sealed class ResizeGrip : Border
{
    /// <summary>被拖的那个宽度。默认双向绑定 —— 拖完要写回视图模型。</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ResizeGrip, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>下限。</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ResizeGrip, double>(nameof(Minimum), 60d);

    /// <summary>上限。</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ResizeGrip, double>(nameof(Maximum), double.PositiveInfinity);

    /// <summary>反向:往左拖是变宽(手柄贴在被拖对象的**左**边时用)。</summary>
    public static readonly StyledProperty<bool> InvertedProperty =
        AvaloniaProperty.Register<ResizeGrip, bool>(nameof(Inverted));

    private Visual? _frame;
    private double _startValue;
    private double _startX;

    static ResizeGrip()
    {
        // 默认值而不是构造里赋值:构造里写就成了本地值,样式里的 :pointerover 再也压不过它。
        BackgroundProperty.OverrideDefaultValue<ResizeGrip>(Brushes.Transparent);
        WidthProperty.OverrideDefaultValue<ResizeGrip>(6d);
        CursorProperty.OverrideDefaultValue<ResizeGrip>(new Cursor(StandardCursorType.SizeWestEast));
    }

    /// <summary>被拖的那个宽度。</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>下限。</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>上限。</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>往左拖是不是变宽。</summary>
    public bool Inverted
    {
        get => GetValue(InvertedProperty);
        set => SetValue(InvertedProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _frame = TopLevel.GetTopLevel(this);
        if (_frame is null)
        {
            return;
        }
        _startX = e.GetPosition(_frame).X;
        _startValue = Value;
        e.Pointer.Capture(this);
        PseudoClasses.Set(":pressed", true);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_frame is null || !ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }
        double delta = e.GetPosition(_frame).X - _startX;
        double upper = Maximum > Minimum ? Maximum : Minimum;
        Value = Math.Clamp(_startValue + (Inverted ? -delta : delta), Minimum, upper);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        PseudoClasses.Set(":pressed", false);
        _frame = null;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        PseudoClasses.Set(":pressed", false);
        _frame = null;
    }
}
