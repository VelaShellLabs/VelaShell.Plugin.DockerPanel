using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ContainersPageView 的视图。</summary>
public sealed partial class ContainersPageView : UserControl
{
    /// <summary>列头上那条拖拽轨道的宽度。与 XAML 里的 6 是同一个数。</summary>
    private const double SplitterWidth = 6;

    /// <summary>列宽之外那些固定占位:状态色条 + 勾选框 + 行尾动作 + 一点余量。</summary>
    private const double ChromeWidth = 3 + 26 + 92 + 8;

    /// <summary>抽屉分割条那一列的宽度(与宿主侧栏同款的 5px 轨道)。</summary>
    private static readonly GridLength SplitterTrack = new(5);

    private static readonly GridLength Collapsed = new(0);

    private readonly Dictionary<string, double> _startWidths = [];
    private readonly Grid _root;
    private readonly Grid _headerGrid;
    private ContainersPageViewModel? _page;
    private string? _activeSplitter;
    private double _dragStartX;
    private double _rootWidth;

    /// <summary>建视图。</summary>
    public ContainersPageView()
    {
        AvaloniaXamlLoader.Load(this);
        // 按名字取而不是用生成的字段:这个面板走的是运行时装载,拿不到编译期生成的那几个字段。
        _root = this.GetControl<Grid>("Root");
        _headerGrid = this.GetControl<Grid>("HeaderGrid");
        // 隧道式挂在视图根上:指针捕获在**视图**上(而不是那条 6px 的窄边框上),
        // 拖出轨道之外仍然收得到移动 —— 与宿主文件浏览器的列拖拽同一路数。
        AddHandler(PointerMovedEvent, OnColumnSplitterPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnColumnSplitterReleased, RoutingStrategies.Tunnel);
        this.GetControl<GridSplitter>("DrawerSplitter").DragCompleted += OnDrawerDragCompleted;
    }

    // ── 详情抽屉:列定义由代码管 ────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is { } previous)
        {
            previous.PropertyChanged -= OnPagePropertyChanged;
        }
        _page = DataContext as ContainersPageViewModel;
        if (_page is { } page)
        {
            page.PropertyChanged += OnPagePropertyChanged;
        }
        ApplyDrawerLayout();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContainersPageViewModel.HasDetail)
            or nameof(ContainersPageViewModel.DetailMaximized)
            or nameof(ContainersPageViewModel.DrawerWidth))
        {
            ApplyDrawerLayout();
        }
    }

    /// <summary>面板一变宽窄,抽屉的上限就得重算一遍。</summary>
    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _rootWidth = e.NewSize.Width;
        ApplyDrawerLayout();
    }

    /// <summary>
    /// 把"抽屉开着没有、最大化没有、多宽"翻译成外层那三列的宽度。
    /// <para>
    /// 上限留 360px 给列表:抽屉比面板还宽的话,它的头(还原 / 关闭)会被顶出可视区,
    /// 而那是回到列表的唯一入口;抽屉与列表同屏对照,本来也是这个布局存在的理由。
    /// </para>
    /// </summary>
    private void ApplyDrawerLayout()
    {
        if (_page is not { } page || _root.ColumnDefinitions.Count < 3)
        {
            return;
        }
        ColumnDefinition list = _root.ColumnDefinitions[0];
        ColumnDefinition splitter = _root.ColumnDefinitions[1];
        ColumnDefinition drawer = _root.ColumnDefinitions[2];

        if (!page.HasDetail)
        {
            list.Width = new(1, GridUnitType.Star);
            splitter.Width = Collapsed;
            drawer.Width = Collapsed;
            drawer.MinWidth = 0;
            return;
        }
        if (page.DetailMaximized)
        {
            list.Width = Collapsed;
            splitter.Width = Collapsed;
            drawer.Width = new(1, GridUnitType.Star);
            drawer.MinWidth = 0;
            drawer.MaxWidth = double.PositiveInfinity;
            return;
        }
        double min = ContainersPageViewModel.MinimumDrawerWidth;
        double max = Math.Max(min, _rootWidth - 360);
        list.Width = new(1, GridUnitType.Star);
        splitter.Width = SplitterTrack;
        drawer.MinWidth = min;
        drawer.MaxWidth = max;
        drawer.Width = new(Math.Clamp(page.DrawerWidth, min, max));
    }

    /// <summary>拖完把拖出来的实际宽度写回视图模型 —— 与宿主侧栏那两条分割条一样。</summary>
    private void OnDrawerDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_page is { } page && _root.ColumnDefinitions.Count >= 3)
        {
            double width = _root.ColumnDefinitions[2].ActualWidth;
            if (width > 0)
            {
                page.DrawerWidth = width;
            }
        }
    }

    // ── 列宽:6px 轨道 + 视图级捕获 ──────────────────────────────────────

    /// <summary>
    /// 按下轨道:记下按下那一刻的全部列宽,把指针捕获在视图上。
    /// <para>
    /// 位移一律按"当前指针 − 按下时指针"算,而不是一帧一帧累加 ——
    /// 累加会在列宽被夹住(碰到上下限)之后跑偏,指针和界线就再也对不上了。
    /// </para>
    /// </summary>
    private void OnColumnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag } || _page is not { } page)
        {
            return;
        }
        // 双击 = 按内容自适应,与宿主文件浏览器一致。
        if (e.ClickCount >= 2)
        {
            AutoFitColumn(page, tag);
            e.Handled = true;
            return;
        }
        _activeSplitter = tag;
        _dragStartX = e.GetPosition(this).X;
        _startWidths.Clear();
        foreach (string key in ContainerColumns.Keys)
        {
            _startWidths[key] = page.Columns.GetColumnWidth(key);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnColumnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeSplitter is not { } tag || _page is not { } page ||
            !_startWidths.TryGetValue(tag, out double startWidth))
        {
            return;
        }
        double delta = e.GetPosition(this).X - _dragStartX;
        page.Columns.SetColumnWidth(tag, Math.Clamp(startWidth + delta,
            ContainerColumns.MinWidthFor(tag), MaxWidthFor(page, tag)));
        e.Handled = true;
    }

    private void OnColumnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeSplitter is null)
        {
            return;
        }
        _activeSplitter = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// 一列最宽能到哪:剩下那些列、六条轨道与行首行尾的固定占位都得留出来,
    /// 否则一拖就把行尾那三颗动作按钮挤出可视区。
    /// </summary>
    private double MaxWidthFor(ContainersPageViewModel page, string key)
    {
        double others = ContainerColumns.Keys
            .Where(k => k != key)
            .Sum(page.Columns.GetColumnWidth);
        double available = _headerGrid.Bounds.Width > 0 ? _headerGrid.Bounds.Width : _rootWidth;
        return Math.Max(ContainerColumns.MinWidthFor(key),
            available - others - (ContainerColumns.Keys.Length * SplitterWidth) - ChromeWidth);
    }

    /// <summary>
    /// 双击轨道:把这一列收放到"正好装得下当前这些行"。
    /// <para>
    /// 量的是当前**筛出来的**那些行,不是全部 —— 用户看得见的就是这些,
    /// 为了一行被筛掉的超长镜像名把列撑开,反而看不成。
    /// </para>
    /// </summary>
    private void AutoFitColumn(ContainersPageViewModel page, string key)
    {
        Typeface mono = new(Resource("VelaUiMonoFont") as FontFamily ?? FontFamily.Default);
        Typeface ui = new(FontFamily.Default);
        double size = Resource(key is "name" ? "VelaFontSize12" : "VelaFontSize11") as double? ?? 11;
        double widest = Measure(HeaderFor(key), ui, size);
        foreach (ContainerRow row in page.View)
        {
            widest = Math.Max(widest, Measure(CellText(row, key), key is "name" ? ui : mono, size));
        }
        page.Columns.SetColumnWidth(key, Math.Clamp(widest + PaddingFor(key),
            ContainerColumns.MinWidthFor(key),
            Math.Min(ContainerColumns.MaxAutoFitFor(key), MaxWidthFor(page, key))));
    }

    private static double Measure(string text, Typeface typeface, double size) =>
        text.Length == 0
            ? 0
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.Black).Width;

    private object? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) ? value : null;

    private static string HeaderFor(string key) => key switch
    {
        "name" => "名称",
        "image" => "镜像",
        "ports" => "端口",
        "cpu" => "CPU",
        "mem" => "内存",
        _ => "运行时长"
    };

    private static string CellText(ContainerRow row, string key) => key switch
    {
        "name" => row.Name,
        "image" => row.Image,
        "ports" => row.Ports,
        "cpu" => row.CpuText,
        "mem" => row.MemText,
        _ => row.Uptime
    };

    /// <summary>单元格里除文字之外还占着的宽度:状态点、项目徽标、sparkline、右侧留白。</summary>
    private static double PaddingFor(string key) => key switch
    {
        "name" => 90,
        "cpu" => 74,
        _ => 18
    };

    /// <summary>
    /// 点整行 = 打开详情抽屉。
    /// <para>
    /// 设计稿的行尾只有三颗动作按钮,没有单独的"详情"按钮 —— 行本身就是那颗按钮。
    /// 但行里还坐着勾选框和三颗动作按钮,它们的 Tapped 会一路冒泡到这里;
    /// 所以先看事件是不是从某个按钮里出来的,是就让开。
    /// </para>
    /// </summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source &&
            (source.FindAncestorOfType<Button>(true) is not null ||
             source.FindAncestorOfType<CheckBox>(true) is not null))
        {
            return;
        }
        if (sender is Control { DataContext: ContainerRow row } &&
            DataContext is ContainersPageViewModel page)
        {
            page.OpenDetailCommand.Execute(row);
        }
    }
}
