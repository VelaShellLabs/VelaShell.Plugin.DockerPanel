using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 面板用到的值转换器。
/// <para>
/// 全部走宿主的 <c>Vela*</c> 令牌:转换器只负责"这一行是什么状态",
/// 具体是哪个颜色由主题字典决定 —— 切明暗时不需要面板做任何事。
/// </para>
/// </summary>
public static class Converters
{
    /// <summary>状态色 → 画刷。参数可选 <c>dim</c>,取半透明底色。</summary>
    public static readonly IValueConverter ToneBrush = new ToneBrushConverter();

    /// <summary>状态色 → 图标资源键。</summary>
    public static readonly IValueConverter ToneIcon = new FuncValueConverter<RowTone, string>(tone => tone switch
    {
        RowTone.Ok => "Icon.circle-check",
        RowTone.Warn => "Icon.triangle-alert",
        RowTone.Danger => "Docker.circle-x",
        RowTone.Busy => "Icon.refresh-cw",
        _ => "Icon.circle-help"
    });

    /// <summary>后果说明的严重度 → 画刷。</summary>
    public static readonly IValueConverter SeverityBrush = new SeverityBrushConverter();

    /// <summary>后果说明的严重度 → 图标。</summary>
    public static readonly IValueConverter SeverityIcon = new FuncValueConverter<int, string>(severity => severity switch
    {
        1 => "Icon.shield",
        2 => "Icon.triangle-alert",
        3 => "Docker.circle-x",
        _ => "Icon.info"
    });

    /// <summary>反馈语气 → 画刷。</summary>
    public static readonly IValueConverter FeedbackBrush = new FeedbackBrushConverter();

    /// <summary>0–1 的比例 → 星形宽度(进度条的已填充段)。</summary>
    public static readonly IValueConverter RatioStar =
        new FuncValueConverter<double, GridLength>(v => new(Math.Clamp(v, 0, 1), GridUnitType.Star));

    /// <summary>0–1 的比例 → 剩余段的星形宽度。</summary>
    public static readonly IValueConverter RatioRestStar =
        new FuncValueConverter<double, GridLength>(v => new(1 - Math.Clamp(v, 0, 1), GridUnitType.Star));

    /// <summary>把 0–100 的采样值换算成 sparkline 里的柱高(最高 16)。</summary>
    public static readonly IValueConverter SampleHeight =
        new FuncValueConverter<double, double>(v => Math.Max(2, Math.Clamp(v, 0, 100) / 100 * 16));

    /// <summary>把 0–100 的采样值换算成趋势图里的柱高(最高 96)。</summary>
    public static readonly IValueConverter TrendHeight =
        new FuncValueConverter<double, double>(v => Math.Max(2, Math.Clamp(v, 0, 100) / 100 * 96));

    /// <summary>0–1 的比例 → 剩下那一段的权重(配合 <see cref="WeightedStackPanel" />)。</summary>
    public static readonly IValueConverter RemainingWeight =
        new FuncValueConverter<double, double>(v => Math.Max(0.0001, 1 - Math.Clamp(v, 0, 1)));

    /// <summary>
    /// 值为 0(含极小的浮点残留)为 true。进度条据此在"不知道分母"时退化成不确定型。
    /// </summary>
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<double, bool>(v => v <= 0.0001);

    /// <summary>
    /// 来源序号 → 一个稳定的颜色。
    /// <para>
    /// 用宿主已有的语义色轮着来,不自造调色板 —— 它们在明暗两套主题下都已经调过对比度,
    /// 而临时挑的六个 hex 在亮色模式下多半有一半看不清。
    /// </para>
    /// </summary>
    public static readonly IValueConverter SourceBrush = new SourceBrushConverter();

    /// <summary>键盘选中项的底色。</summary>
    public static readonly IValueConverter ActiveBackground = new ActiveBackgroundConverter();

    /// <summary>非空字符串为 true。</summary>
    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(v => !string.IsNullOrEmpty(v));

    /// <summary>取反。</summary>
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(v => !v);

    private static IBrush Resolve(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush brush
            ? brush
            : fallback;

    private sealed class ToneBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool dim = parameter as string == "dim";
            string key = value switch
            {
                RowTone.Ok => dim ? "VelaShellGreenDim" : "VelaStatusConnected",
                RowTone.Warn => dim ? "VelaShellYellowDim" : "VelaWarning",
                RowTone.Danger => dim ? "VelaShellRedDim" : "VelaError",
                RowTone.Busy => dim ? "VelaAccentDim" : "VelaAccent",
                _ => dim ? "VelaShellSubtleDim" : "VelaTextTertiary"
            };
            return Resolve(key, Brushes.Gray);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Resolve("VelaAccentDim", Brushes.Transparent) : Brushes.Transparent;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class SourceBrushConverter : IValueConverter
    {
        /// <summary>轮换用的令牌。六个之后回头 —— 同屏合并超过六条流本来就读不动了。</summary>
        private static readonly string[] Palette =
        [
            "VelaAccent", "VelaStatusConnected", "VelaInfo",
            "VelaWarning", "VelaShellMagenta", "VelaShellCyan"
        ];

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int index = value is int i && i >= 0 ? i : 0;
            return Resolve(Palette[index % Palette.Length], Brushes.Gray);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class SeverityBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Resolve(value switch
            {
                1 => "VelaStatusConnected",
                2 => "VelaWarning",
                3 => "VelaError",
                _ => "VelaTextTertiary"
            }, Brushes.Gray);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class FeedbackBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool dim = parameter as string == "dim";
            string key = value switch
            {
                FeedbackKind.Success => dim ? "VelaShellGreenDim" : "VelaStatusConnected",
                FeedbackKind.Warning => dim ? "VelaShellYellowDim" : "VelaWarning",
                FeedbackKind.Error => dim ? "VelaShellRedDim" : "VelaError",
                _ => dim ? "VelaAccentDim" : "VelaAccent"
            };
            return Resolve(key, Brushes.Gray);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// 按资源键取图标几何。
/// <para>
/// 图标名是数据(视图模型给的字符串),而 <c>StreamGeometry</c> 在资源字典里 ——
/// 这个转换器把两者接上,免得每处都写一个 <c>DataTrigger</c> 大表。
/// </para>
/// </summary>
public sealed class IconLookupConverter : IValueConverter
{
    /// <summary>单例。</summary>
    public static readonly IconLookupConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return null;
        }
        return Application.Current?.TryFindResource(key, out object? geometry) == true ? geometry : null;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 会丢数据那一档的闸门用红边框,一般破坏性用常规描边。
/// <para>
/// 边框颜色是两档之间**唯一**不靠文字的区分 —— 用户在读标题之前就该感觉到分量不同。
/// </para>
/// </summary>
public sealed class DataLossBorderConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaError" : "VelaBorderSecondary";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>只读字段的底色比可编辑的暗一档 —— 不用读提示就知道这一格改不了。</summary>
public sealed class ReadOnlyBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaBgSurface" : "VelaBgInput";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>危险开关的说明文字用警示色。</summary>
public sealed class DangerTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaError" : "VelaTextTertiary";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>跟随中的按钮用绿底,停了就是透明。</summary>
public sealed class FollowBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            return Brushes.Transparent;
        }
        return Application.Current?.TryFindResource("VelaShellGreenDim", out object? brush) == true
            ? brush
            : Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>跟随中的文字与图标用绿色。</summary>
public sealed class FollowForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaStatusConnected" : "VelaTextSecondary";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 搜索命中的行整行加一层淡黄底。
/// <para>
/// 不做行内高亮是有意的:日志行是纯文本(<c>SelectableTextBlock</c> 才能复制),
/// 把它拆成几段富文本会同时毁掉选中与复制 —— 而那两件事在日志里比高亮更常用。
/// </para>
/// </summary>
public sealed class MatchBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            return Brushes.Transparent;
        }
        return Application.Current?.TryFindResource("VelaShellYellowDim", out object? brush) == true
            ? brush
            : Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>标准错误的行用红色,标准输出用终端前景色。</summary>
public sealed class LogLineForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaShellRed" : "VelaShellWhite";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>仓库凭据状态的图标:拿到了凭据用锁,没拿到用警示 —— 它决定拉取会不会 401。</summary>
public sealed class AuthIconConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Docker.lock" : "Icon.triangle-alert";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>仓库凭据状态的颜色。</summary>
public sealed class AuthBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaStatusConnected" : "VelaWarning";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>悬空镜像用虚线圈,有标签的用层叠图标。</summary>
public sealed class ImageIconConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Docker.circle-dashed" : "Icon.layers";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>执行记录的行色:命令行绿、标准错误红、其余是终端前景色。</summary>
public sealed class OutputLineForegroundConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isError = values.Count > 0 && values[0] is true;
        bool isCommand = values.Count > 1 && values[1] is true;
        string key = isCommand ? "VelaStatusConnected" : isError ? "VelaShellRed" : "VelaShellWhite";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }
}

/// <summary>按资源键取画刷(堆叠占用条的每一段各用一个令牌)。</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key)
        {
            return Brushes.Transparent;
        }
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>危险档的回收卡片用红边,其余用常规边。</summary>
public sealed class PruneBorderConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is RowTone.Danger ? "VelaShellRedDim" : "VelaBorderPrimary";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>高负载的条与数字转警示色。</summary>
public sealed class HotBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is true ? "VelaGaugeWarn" : "VelaGaugeCpu";
        return Application.Current?.TryFindResource(key, out object? brush) == true ? brush : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
