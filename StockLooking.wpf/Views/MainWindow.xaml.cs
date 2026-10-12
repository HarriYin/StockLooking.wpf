using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;

// 歧义消解：隐式 using 引入了 System.Drawing，与 WPF 同名类型冲突
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WButton = System.Windows.Controls.Button;
using WCombo = System.Windows.Controls.ComboBox;

namespace StockLooking.wpf.Views;

/// <summary>
/// 主窗口：盯盘设置 + 行情明细（K线/分时，内嵌 KlineChart）+ 分组自选（同花顺式）
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _liveApply;
    private readonly DispatcherTimer _autoSave;

    /// <summary>
    /// 自选股列表的行集合。用 <see cref="ObservableCollection{T}"/> 而非 List：
    /// 拖拽/上移下移排序时可以用 <c>Move</c> 让 ListBox **复用容器**（不整表重建），
    /// 才能在此基础上做 FLIP 过渡动画（重建容器就没有"上一帧位置"可言了）。
    /// </summary>
    private readonly ObservableCollection<WatchRow> _watchRows = new();
    private List<WatchRow> _detailRows = new();
    private string _sectorFilter = "全部";

    /// <summary>自选股表头当前的右侧补偿留白（= 列表垂直滚动条占用宽度；用于避免重复设置 Margin）</summary>
    private double _stockHeaderGutter;

    // 输入框智能提示：300ms 防抖 + 请求序号防乱序
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _searchSeq;
    private bool _suppressSearchText;

    // ===== 分组自选（同花顺式）=====
    // 各分组共享同一份 WatchRow 实例（同一代码只持有一份），行情刷新就地更新即驱动所有分组卡片同步刷新
    private readonly ObservableCollection<GroupViewModel> _groupViewModels = new();
    private readonly Dictionary<string, WatchRow> _groupRowByCode = new(StringComparer.OrdinalIgnoreCase);
    private GroupViewModel? _selectedGroup;
    private WatchRow? _selectedRow;
    private StockSearchItem? _pendingAddItem;

    /// <summary>分组自选页自动刷新定时器（仅在该页可见时运行）</summary>
    private readonly DispatcherTimer _groupRefresh;

    /// <summary>XAML 加载期间为 true：此时 _settings 已赋值但同级控件尚未创建，须抑制控件事件</summary>
    private bool _initializing = true;

    /// <summary>允许真正关闭（退出应用时置 true）</summary>
    private bool _allowClose;

    public event EventHandler<AppSettings>? SettingsChanged;

    public MainWindow(AppSettings settings)
    {
        // 必须在 InitializeComponent 之前赋值：Slider 在 XAML 中因 Minimum 被钳制会立即触发 ValueChanged
        _settings = settings;
        InitializeComponent();

        // 主界面默认尺寸在 XAML 里按「宽 +41% / 高 +19%」放大（980×720 → 1380×860，约 16:10 视觉比例）。
        // 这里按当前屏幕工作区再钳制一次，避免在低分辨率/多屏小工作区上越界。
        // WorkArea 与 Window.Width/Height 同为 DIP 单位，无需 DPI 换算；
        // 窗口为无边框自绘标题栏，尺寸变化由内部 Grid 的 * 行/列全自适应承接（含 K 线图与多股K线页）。
        var work = System.Windows.SystemParameters.WorkArea;
        if (Width > work.Width * 0.94) Width = work.Width * 0.94;
        if (Height > work.Height * 0.94) Height = work.Height * 0.94;

        // 滑杆拖动会高频触发，做 250ms 去抖后再通知盯盘窗口，避免频繁重建 UI
        _liveApply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveApply.Tick += (_, _) =>
        {
            _liveApply.Stop();
            RaiseSettingsChanged();
        };

        // 自选股增删 / 勾选变化后自动落盘；500ms 去抖，避免连续点击反复写文件
        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoSave.Tick += (_, _) =>
        {
            _autoSave.Stop();
            SaveSettings("已自动保存");
        };

        // 分组自选页自动刷新：间隔复用盯盘刷新间隔（下限 2s，避免过于频繁打接口）；
        // 是否真正运行由 UpdateGroupRefreshTimer 决定（主窗可见 + 停留本页 + 已勾选）
        _groupRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(2000, settings.RefreshIntervalMs)) };
        _groupRefresh.Tick += (_, _) => _ = RefreshGroupQuotesAsync();

        // K线刷新由内嵌 KlineChart 自管定时器驱动（主窗可见且在 K线 页时由 UpdateKlineTimer 激活）
        IsVisibleChanged += (_, _) =>
        {
            UpdateKlineTimer();
            UpdateGroupRefreshTimer();
            UpdateOverviewTimer();
        };

        // 输入框智能提示：停止输入 300ms 后才真正请求
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await DoSearchAsync();
        };

        LoadControls();
        _initializing = false;
        UpdateGroupRefreshTimer();
        UpdateOverviewTimer();
    }

    private void LoadControls()
    {
        SectorFilterCombo.ItemsSource = StockSector.FilterOptions;
        SectorFilterCombo.SelectedIndex = 0;   // "全部"

        RefreshWatchList();

        RefreshSlider.Value = Math.Clamp(_settings.RefreshIntervalMs, RefreshSlider.Minimum, RefreshSlider.Maximum);
        RefreshText.Text = _settings.RefreshIntervalMs + " ms";
        FontSlider.Value = Math.Clamp(_settings.FontSize, FontSlider.Minimum, FontSlider.Maximum);
        FontText.Text = _settings.FontSize.ToString("F0");
        BgOpacitySlider.Value = Math.Clamp(_settings.BackgroundOpacity, BgOpacitySlider.Minimum, BgOpacitySlider.Maximum);
        BgOpacityText.Text = _settings.BackgroundOpacity.ToString("F2");
        TextOpacitySlider.Value = Math.Clamp(_settings.TextOpacity, TextOpacitySlider.Minimum, TextOpacitySlider.Maximum);
        TextOpacityText.Text = _settings.TextOpacity.ToString("F2");
        HoverChk.IsChecked = _settings.HoverToShow;
        TopChk.IsChecked = _settings.AlwaysOnTop;
        MinimalChk.IsChecked = _settings.MinimalMode;

        // 回填主题下拉：默认「跟随系统」；若不回填，打开设置页时该下拉会显示为空白
        ThemeCombo.SelectedItem = ThemeCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, _settings.Theme.ToString(), StringComparison.Ordinal))
            ?? ThemeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();

        FieldsGrid.ItemsSource = _settings.Fields;
        // 颜色模式的下拉选项定义在 XAML 的静态资源 ColorModeChoices 里（模板列内部按 StaticResource 引用），
        // 此处不再给列赋 ItemsSource（原来的 DataGridComboBoxColumn 会套系统样式，已改为模板列）。

        RefreshDetailStocks();
        BuildGroupViewModels();

        // 内嵌 KlineChart：注入配置（周期/自动刷新写回 settings 并通过 onDirty 自动保存）+ 初始股票
        Kline.Init(_settings, QueueLiveApply);
        var firstDetail = _detailRows.FirstOrDefault();
        if (firstDetail != null) Kline.SetStock(firstDetail.Code, firstDetail.Name);
        Kline.SetActive(false);

        // 多股K线页：独立控件（容器 + 叠加绘制）；自动刷新是**页面级**开关（MultiKlineAutoRefresh）
        MultiKline.Init(_settings, QueueAutoSave);

        // 全球市场页：港/台/美大盘 + 外围个股检查（数据/自动刷新由页内自管）
        GlobalView.Init(_settings, QueueAutoSave);

        // 大盘概览页：全市场涨跌分布 + 指数卡片
        Overview.Init(_settings, QueueAutoSave);

        // 分组搜索框：选中候选 → 弹出「加入哪些分组」选择
        GroupSearchBox.StockPicked += GroupSearchBox_StockPicked;

        // 回填分组自选页的自动刷新勾选（用 Click 事件，程序赋值不会误触发刷新）
        GroupAutoRefreshChk.IsChecked = _settings.GroupAutoRefresh;

        // 按"当前停留的分页"对齐各页的自动刷新勾选（只勾当前页；未选中的页一律取消）
        SyncAutoRefreshByTab();
    }

    /// <summary>刷新明细页的股票下拉（含股票名称与板块；股票池变更后调用）</summary>
    public void RefreshDetailStocks()
    {
        var selectedCode = (DetailStockCombo.SelectedItem as WatchRow)?.Code;

        _detailRows = _settings.Stocks
            .Select(TencentStockClient.NormalizeCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Select(code =>
            {
                var s = StockSector.Classify(code);
                var known = _watchRows.FirstOrDefault(r => r.Code == code);
                return new WatchRow
                {
                    Code = code,
                    Name = known?.Name ?? "",
                    SectorName = s.Name,
                    SectorColor = s.Color,
                    SectorOrder = s.Order
                };
            })
            // 顺序与「盯盘设置」自选股列表保持一致（Settings.Stocks 本身按用户拖拽顺序持久化）
            .ToList();

        DetailStockCombo.ItemsSource = _detailRows;
        if (_detailRows.Count == 0) return;

        // 程序化回填下拉选中项：抑制 SelectionChanged（避免回填被当成"用户选股"而触发多余的重新加载）
        _suppressDetailCombo = true;
        try
        {
            DetailStockCombo.SelectedItem = selectedCode != null && _detailRows.Any(r => r.Code == selectedCode)
                ? _detailRows.First(r => r.Code == selectedCode)
                : _detailRows[0];
        }
        finally { _suppressDetailCombo = false; }

        // 让图表与最终选择保持一致（股票池增删后下拉可能换了选中的票）
        if (DetailStockCombo.SelectedItem is WatchRow cur && IsVisible)
            Kline.SetStock(cur.Code, cur.Name);

        _ = LoadDetailNamesAsync();
    }

    /// <summary>补齐明细下拉中缺失的股票名称（WatchRow 实现 INPC，赋值后界面自动刷新）</summary>
    private async System.Threading.Tasks.Task LoadDetailNamesAsync()
    {
        var missing = _detailRows.Where(r => string.IsNullOrWhiteSpace(r.Name)).Select(r => r.Code).ToList();
        if (missing.Count == 0) return;
        try
        {
            var qs = await TencentStockClient.GetQuotesAsync(missing);
            foreach (var q in qs)
            {
                var row = _detailRows.FirstOrDefault(r => r.Code == q.Code);
                if (row != null && !string.IsNullOrWhiteSpace(q.Name)) row.Name = q.Name;
            }
        }
        catch
        {
            // 名称拉取失败则下拉退回显示代码，不影响功能
        }
    }

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, _settings);

    private void QueueLiveApply()
    {
        if (_initializing) return;
        _liveApply.Stop();
        _liveApply.Start();
    }

    /// <summary>自选股增删 / 勾选变化后排队自动保存（500ms 去抖，避免连点反复写盘）</summary>
    private void QueueAutoSave()
    {
        if (_initializing) return;
        _autoSave.Stop();
        _autoSave.Start();
    }

    /// <summary>若存在待执行的自动保存，立即落盘（退出前调用，避免丢掉最后一次改动）</summary>
    private void FlushAutoSave()
    {
        if (!_autoSave.IsEnabled) return;
        _autoSave.Stop();
        SaveSettings("已自动保存");
    }

    /// <summary>写入配置文件：「保存设置」按钮与自动保存共用</summary>
    private void SaveSettings(string hintPrefix)
    {
        NormalizeStocks();
        SettingsService.Save(_settings);
        SaveHint.Text = hintPrefix + " " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>
    /// 原地整理股票池与隐藏名单（规范化代码、去重、剔除已不存在的项）。
    /// 必须原地改（Clear + AddRange）以保持 List 实例不变 —— ItemsSource 与盯盘窗口共用同一引用。
    /// </summary>
    private void NormalizeStocks()
    {
        var snapshot = _settings.Stocks
            .Select(TencentStockClient.NormalizeCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        _settings.Stocks.Clear();
        _settings.Stocks.AddRange(snapshot);
        _settings.HiddenStocks = _settings.HiddenStocks.Where(c => snapshot.Contains(c)).Distinct().ToList();
    }

    #region 自选股（行情展示 + 板块筛选 + 中文搜索）
    private void RefreshWatchList()
    {
        var rows = _settings.Stocks
            .Select(TencentStockClient.NormalizeCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Select(code =>
            {
                var s = StockSector.Classify(code);
                return new WatchRow
                {
                    Code = code,
                    SectorName = s.Name,
                    SectorColor = s.Color,
                    SectorOrder = s.Order,
                    IsSelected = !_settings.HiddenStocks.Contains(code)
                };
            });

        // 板块筛选
        if (_sectorFilter != "全部") rows = rows.Where(r => r.SectorName == _sectorFilter);

        // 顺序 = 用户在列表中排定的顺序（支持拖拽排序与「上移 / 下移」）。
        // 这里**绝不能再按板块/代码排序** —— 那样 _settings.Stocks 的顺序会被覆盖，自定义排序完全失效。
        // （板块筛选只是保序过滤，不影响顺序。）
        // 就地替换内容（不换集合实例）→ ListBox 的 ItemsSource 始终指向同一个 ObservableCollection
        _watchRows.Clear();
        foreach (var r in rows) _watchRows.Add(r);
        StocksList.ItemsSource = _watchRows;
        UpdateStockCount();
        _ = LoadWatchQuotesAsync();
    }

    /// <summary>
    /// 盯盘窗口拖拽调整顺序后由 App 调用：刷新自选股列表与明细下拉，使两处顺序保持一致。
    /// （_settings 为同一实例，顺序本身已生效，这里只做界面同步。）
    /// </summary>
    public void SyncWatchOrder()
    {
        RefreshWatchList();
        RefreshDetailStocks();
    }

    /// <summary>
    /// 托盘菜单切换极简模式后回填勾选框。置 _initializing 以免触发 MinimalChk_Changed
    /// （那条路径会再写一遍设置并推送应用，形成回环）。
    /// </summary>
    public void SyncMinimalMode(bool on)
    {
        var prev = _initializing;
        _initializing = true;
        try { MinimalChk.IsChecked = on; }
        finally { _initializing = prev; }
    }

    private void UpdateStockCount()
    {
        var total = _settings.Stocks.Count;
        var shown = _watchRows.Count;
        var checkedCount = _watchRows.Count(r => r.IsSelected);
        StockCountText.Text = _sectorFilter == "全部"
            ? $"共 {shown} 只（勾选 {checkedCount} 只）"
            : $"筛选 {shown} / 共 {total} 只（勾选 {checkedCount} 只）";
    }

    /// <summary>拉取自选股实时行情并填充列表（名称 + 股价/涨跌%/量/额/换手）</summary>
    private async System.Threading.Tasks.Task LoadWatchQuotesAsync()
    {
        if (_watchRows.Count == 0) { StocksList.Items.Refresh(); return; }
        try
        {
            var qs = await TencentStockClient.GetQuotesAsync(_watchRows.Select(r => r.Code));
            foreach (var q in qs)
            {
                var row = _watchRows.FirstOrDefault(r => r.Code == q.Code);
                if (row == null) continue;
                row.ApplyQuote(q);
            }
            StocksList.Items.Refresh();
        }
        catch
        {
            // 行情拉取失败则保留上一帧，不影响其它功能
        }
    }

    private void RefreshQuotes_Click(object sender, RoutedEventArgs e) => _ = LoadWatchQuotesAsync();

    /// <summary>向下找第一个指定类型的视觉子元素（用于从 ListBox 命中内部 ScrollViewer）</summary>
    private static T? FindVisualChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (child is T t) return t;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// 自选股表头与行内容的水平对齐补偿。
    /// 列宽全部用 * 权重分配：表头在 ListBox 之外，而 ListBox 一旦需要垂直滚动条，
    /// 行内容的可用宽度就少掉「细滚动条 10px + 边距 6px」= 16px；表头若不补同样的右侧留白，两边会逐列错开。
    /// 用 LayoutUpdated 驱动（只有布局完成后才知道是否出现滚动条），取值不变时直接返回，开销可忽略。
    /// </summary>
    private void StocksList_LayoutUpdated(object? sender, EventArgs e)
    {
        var sv = FindVisualChild<System.Windows.Controls.ScrollViewer>(StocksList);
        var gutter = (sv != null && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible) ? 16.0 : 0.0;
        if (Math.Abs(gutter - _stockHeaderGutter) < 0.5) return;
        _stockHeaderGutter = gutter;
        StockHeaderGrid.Margin = new Thickness(2, 0, 2 + gutter, 4);
    }

    private void SectorFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (SectorFilterCombo.SelectedItem is SectorInfo si)
        {
            _sectorFilter = si.Name;
            RefreshWatchList();
        }
    }

    private void AddStock_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var raw = NewStockBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        AddCodes(raw.Split(',', StringSplitOptions.RemoveEmptyEntries));
        ClearSearchBox();
    }

    private void RemoveStock_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (StocksList.SelectedItem is WatchRow sel)
        {
            _settings.Stocks.Remove(sel.Code);
            _settings.HiddenStocks.Remove(sel.Code);
            RefreshWatchList();
            RefreshDetailStocks();
            QueueLiveApply();
            QueueAutoSave();
        }
    }

    #region 自选股排序：行内拖拽手柄 + 上移 / 下移

    private WatchRow? _dragRow;
    private System.Windows.Point _dragStart;
    private System.Windows.Controls.ListBoxItem? _dragContainer;   // 被拖拽行的容器
    private double _dragGrabOffset;       // 按下点相对行顶部的偏移，保持"抓在原处"的手感
    private double _dragPointerY;         // 鼠标当前 Y（相对 StocksList），驱动跟手
    private double _dragLastY;            // 上一次触发换位的 Y（阈值防抖）
    private string? _lastDropTarget;      // 上一次换位目标代码，避免边界抖动反复换位
    private bool _isDragging;             // 是否已触发真正拖拽移动
    private System.Windows.Media.Brush? _dragOrigBackground;
    private System.Windows.Media.Effects.Effect? _dragOrigEffect;

    /// <summary>仅当按在行右侧的「拖拽手柄」上才允许拖动（手柄默认透明，但仍参与命中测试）</summary>
    private void StocksList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragRow = null;
        _dragContainer = null;
        _isDragging = false;
        if (!IsUnderDragHandle(e.OriginalSource as DependencyObject)) return;
        var row = FindDataContext<WatchRow>(e.OriginalSource as DependencyObject);
        if (row == null) return;
        var container = StocksList.ItemContainerGenerator.ContainerFromItem(row) as System.Windows.Controls.ListBoxItem;
        if (container == null) return;

        _dragRow = row;
        _dragContainer = container;
        _dragStart = e.GetPosition(StocksList);
        _dragGrabOffset = e.GetPosition(container).Y;
        _dragPointerY = _dragStart.Y;
        _dragLastY = _dragStart.Y;
        _lastDropTarget = row.Code;

        // 捕获鼠标：拖出 ListBox 边界仍能收到 Move/Up
        StocksList.CaptureMouse();
        e.Handled = true;   // 避免触发 ListBox 行选中
    }

    private void StocksList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragRow == null || _dragContainer == null) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { EndDrag(); return; }

        var p = e.GetPosition(StocksList);
        if (!_isDragging)
        {
            if (Math.Abs(p.Y - _dragStart.Y) < 4) return;   // 位移阈值：避免把一次点击误判为拖动
            _isDragging = true;
            StartDragMask(_dragContainer);
            System.Windows.Controls.Panel.SetZIndex(_dragContainer, 1);
        }

        _dragPointerY = p.Y;
        FollowPointer();

        if (Math.Abs(p.Y - _dragLastY) < 4) return;
        _dragLastY = p.Y;

        var targetCode = RowCodeAtY(p.Y);
        if (!string.IsNullOrEmpty(targetCode) && targetCode != _dragRow.Code && targetCode != _lastDropTarget)
        {
            ReorderByDrag(targetCode);
            _lastDropTarget = targetCode;
        }
    }

    private void StocksList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragRow == null) return;
        e.Handled = true;
        EndDrag();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (StocksList.SelectedItem is not WatchRow sel) return;
        var i = _watchRows.IndexOf(sel);
        if (i <= 0) return;
        MoveRowTo(sel, i - 1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (StocksList.SelectedItem is not WatchRow sel) return;
        var i = _watchRows.IndexOf(sel);
        if (i < 0 || i >= _watchRows.Count - 1) return;
        MoveRowTo(sel, i + 1);
    }

    /// <summary>
    /// 把某行移动到指定的「视图位置」。视图顺序即 _settings.Stocks 的顺序（板块筛选只是保序过滤），
    /// 因此以目标位置那一行的代码为锚点，换算成 Stocks 中的真实索引后再插入。
    ///
    /// 排序过程用 FLIP（First–Last–Invert–Play）做过渡动画，与盯盘浮窗的拖拽排序观感一致：
    /// 先记录各行**视觉位置**（含上一次未播完的位移）→ 数据换位 → 记录**布局位置** →
    /// 用 TranslateTransform 把各行瞬时拉回旧位置，再用缓动动画归零。
    /// </summary>
    private void MoveRowTo(WatchRow row, int newViewIndex)
    {
        if (!MoveRowToCore(row, newViewIndex, excludeCode: null)) return;

        RefreshDetailStocks();

        // 移动后把选中恢复到原股票，方便连续点「上移 / 下移」多位
        var again = _watchRows.FirstOrDefault(r => r.Code == row.Code);
        if (again != null) StocksList.SelectedItem = again;
        QueueLiveApply();
        QueueAutoSave();
    }

    /// <summary>
    /// MoveRowTo 的核心：执行换位 + FLIP 动画。返回值表示是否真的发生了换位。
    /// <paramref name="excludeCode"/> 用于拖拽过程中：被拖拽行自身已由 FollowPointer 定位，
    /// 不参与 FLIP 补间，避免它与鼠标"较劲"。
    /// </summary>
    private bool MoveRowToCore(WatchRow row, int newViewIndex, string? excludeCode)
    {
        if (_watchRows.Count == 0) return false;
        newViewIndex = Math.Clamp(newViewIndex, 0, _watchRows.Count - 1);

        var realFrom = _settings.Stocks.IndexOf(row.Code);
        if (realFrom < 0) return false;
        var realTarget = _settings.Stocks.IndexOf(_watchRows[newViewIndex].Code);
        if (realTarget < 0 || realFrom == realTarget) return false;

        var code = _settings.Stocks[realFrom];

        // ① First：记录各行当前的**视觉** Y（含动画中的临时位移）
        var first = CaptureRowY(withTransform: true);

        // ② 数据换位：_settings.Stocks（持久化顺序）+ _watchRows（视图顺序）
        _settings.Stocks.RemoveAt(realFrom);
        _settings.Stocks.Insert(realTarget, code);

        var oldView = _watchRows.IndexOf(row);
        var newView = FilteredCodes().IndexOf(code);
        if (oldView >= 0 && newView >= 0 && oldView != newView) _watchRows.Move(oldView, newView);
        else RefreshWatchList();   // 极端情况（代码对不上）退回整表重建，至少功能正确

        // ③ Last：布局位置就绪后播放过渡
        StocksList.UpdateLayout();
        PlayRowFlip(first, excludeCode);
        return true;
    }

    /// <summary>与 <see cref="RefreshWatchList"/> 同样的保序过滤（只算代码，不建行对象）</summary>
    private List<string> FilteredCodes()
    {
        IEnumerable<string> codes = _settings.Stocks
            .Select(TencentStockClient.NormalizeCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct();
        if (_sectorFilter != "全部") codes = codes.Where(c => StockSector.Classify(c).Name == _sectorFilter);
        return codes.ToList();
    }

    /// <summary>
    /// 记录各行当前 Y（相对 StocksList）。
    /// <paramref name="withTransform"/>=true 取**视觉**位置（含动画位移，用于 First）；
    /// false 取**布局**位置（扣掉位移，用于 Last）—— 混用会让位移被反复计入、动画错乱。
    /// </summary>
    private Dictionary<string, double> CaptureRowY(bool withTransform)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _watchRows.Count; i++)
        {
            if (StocksList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            var y = fe.TranslatePoint(new System.Windows.Point(0, 0), StocksList).Y;
            if (!withTransform && fe.RenderTransform is TranslateTransform tt) y -= tt.Y;
            map[_watchRows[i].Code] = y;
        }
        return map;
    }

    /// <summary>
    /// FLIP 的 Invert + Play：把各行瞬移到旧位置，再用缓动动画归位到 0。
    /// <paramref name="excludeCode"/> 不为空时，对应行不参与补间（用于拖拽过程中被拎起的行）。
    /// </summary>
    private void PlayRowFlip(Dictionary<string, double> first, string? excludeCode = null)
    {
        for (var i = 0; i < _watchRows.Count; i++)
        {
            var code = _watchRows[i].Code;
            if (code.Equals(excludeCode, StringComparison.OrdinalIgnoreCase)) continue;
            if (StocksList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (!first.TryGetValue(code, out var y0)) continue;   // 新出现的行不做动画

            // ★ 位移按**布局位置**算：把"当前视觉 Y"扣掉正在生效的临时位移，
            //   才能保证 Invert 后视觉位置与 First 完全一致。直接用两个视觉 Y 相减，
            //   会在上一次让位还没播完就再次换位时漏算在飞的位移 → 该行每换一位跳一下。
            var curY = fe.RenderTransform is TranslateTransform curTt ? curTt.Y : 0;
            var delta = y0 - (fe.TranslatePoint(new System.Windows.Point(0, 0), StocksList).Y - curY);
            if (Math.Abs(delta) < 0.5)
            {
                fe.RenderTransform = null;
                continue;
            }

            var tt = fe.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                fe.RenderTransform = tt;
            }
            else
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);   // 停掉上一次未播完的让位
            }

            tt.Y = delta;
            // 280ms 正弦缓出：起步轻、收尾柔（与盯盘浮窗的让位动画同一节奏）
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>拖拽过程中：把被拖拽行实时跟随鼠标（直接设 TranslateTransform，无补间）</summary>
    private void FollowPointer()
    {
        if (_dragRow == null || _dragContainer == null) return;
        var root = _dragContainer;

        // 布局位置 = 当前视觉位置扣掉临时偏移
        var layoutY = root.TranslatePoint(new System.Windows.Point(0, 0), StocksList).Y;
        var tt = root.RenderTransform as TranslateTransform;
        if (tt == null)
        {
            tt = new TranslateTransform();
            root.RenderTransform = tt;
        }
        else
        {
            layoutY -= tt.Y;
            // 只在有残留动画时清一次：每帧调 BeginAnimation(null) 会不停重建动画时钟，纯开销
            if (tt.HasAnimatedProperties) tt.BeginAnimation(TranslateTransform.YProperty, null);
        }

        tt.Y = (_dragPointerY - _dragGrabOffset) - layoutY;
    }

    /// <summary>拖拽过程中：把当前行移到目标行位置，并给其它行播放 FLIP 让位动画</summary>
    private void ReorderByDrag(string targetCode)
    {
        if (_dragRow == null) return;
        var targetRow = _watchRows.FirstOrDefault(r => r.Code == targetCode);
        if (targetRow == null) return;

        MoveRowToCore(_dragRow, _watchRows.IndexOf(targetRow), _dragRow.Code);
        // 顺序已变 → 按新布局重算被拖拽行的跟随偏移，否则它会视觉跳一下
        StocksList.UpdateLayout();
        FollowPointer();
    }

    /// <summary>按 Y 坐标定位鼠标下方的行代码（拖拽落点判定）；使用**布局位置**并排除被拖拽行</summary>
    private string? RowCodeAtY(double y)
    {
        for (var i = 0; i < _watchRows.Count; i++)
        {
            var code = _watchRows[i].Code;
            if (code == _dragRow?.Code) continue;
            if (StocksList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            var top = fe.TranslatePoint(new System.Windows.Point(0, 0), StocksList).Y;
            if (fe.RenderTransform is TranslateTransform tt) top -= tt.Y;   // 用布局位置
            if (y >= top && y <= top + fe.ActualHeight) return code;
        }
        return null;
    }

    /// <summary>给被拖拽行加「背景蒙版 + 阴影」：提示该行已被拎起，并避免半透明文字重叠</summary>
    private void StartDragMask(System.Windows.Controls.ListBoxItem container)
    {
        var bd = FindVisualChild<Border>(container);
        if (bd == null) return;
        _dragOrigBackground = bd.Background;
        _dragOrigEffect = container.Effect;

        var cardColor = (ThemeBrush("CardBgBrush") as SolidColorBrush)?.Color ?? Colors.White;
        bd.Background = new SolidColorBrush(cardColor) { Opacity = 0.22 };
        container.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.38,
            Color = Colors.Black
        };
    }

    /// <summary>移除拖拽蒙版，恢复行原本的底色与效果</summary>
    private void ClearDragMask(System.Windows.Controls.ListBoxItem container)
    {
        var bd = FindVisualChild<Border>(container);
        if (bd != null && _dragOrigBackground != null) bd.Background = _dragOrigBackground;
        container.Effect = _dragOrigEffect;
        _dragOrigBackground = null;
        _dragOrigEffect = null;
    }

    /// <summary>
    /// 结束拖拽排序：释放鼠标捕获、恢复悬停探测、移除蒙版；
    /// 被拖拽行从"跟手偏移"平滑归位到布局位置，并统一触发一次保存与盯盘刷新。
    /// </summary>
    private void EndDrag()
    {
        if (_dragRow == null || _dragContainer == null) return;

        StocksList.ReleaseMouseCapture();
        System.Windows.Controls.Panel.SetZIndex(_dragContainer, 0);
        ClearDragMask(_dragContainer);

        // 松手时通常停在两槽之间，用 200ms 动画滑回槽位，避免硬切
        if (_dragContainer.RenderTransform is TranslateTransform tt && Math.Abs(tt.Y) > 0.5)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            _dragContainer.RenderTransform = null;
        }

        var code = _dragRow.Code;
        _dragRow = null;
        _dragContainer = null;
        _isDragging = false;
        _lastDropTarget = null;

        // 保持选中在刚才拖动的票上，并统一刷新明细 / 持久化
        var again = _watchRows.FirstOrDefault(r => r.Code == code);
        if (again != null) StocksList.SelectedItem = again;
        RefreshDetailStocks();
        QueueLiveApply();
        QueueAutoSave();
    }

    /// <summary>从应用级资源字典取主题 Brush（明暗切换后自动更新）</summary>
    private static System.Windows.Media.Brush? ThemeBrush(string key)
    {
        try { return System.Windows.Application.Current.Resources[key] as System.Windows.Media.Brush; }
        catch { return null; }
    }

    /// <summary>按 Y 坐标定位列表行索引（旧版拖拽落点，上移/下移按钮仍用）；落在空白处时取最近的一端</summary>
    private int ListIndexAtY(double y)
    {
        for (var i = 0; i < _watchRows.Count; i++)
        {
            if (StocksList.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.ListBoxItem lbi)
            {
                var top = lbi.TranslatePoint(new System.Windows.Point(0, 0), StocksList).Y;
                if (y >= top && y <= top + lbi.ActualHeight) return i;
            }
        }
        return y <= 0 ? 0 : _watchRows.Count - 1;
    }

    /// <summary>从命中元素向上找带指定 Tag 的祖先（判断是否按在拖拽手柄上）</summary>
    private static bool IsUnderDragHandle(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && Equals(fe.Tag, "DragHandle")) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>从命中元素向上找指定类型的 DataContext（取手柄所属那一行的数据）</summary>
    private static T? FindDataContext<T>(DependencyObject? d) where T : class
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    #endregion

    #region 分组卡片内股票行的拖拽排序（跟手 + FLIP 让位，与盯盘自选股列表同一套观感）

    /// <summary>
    /// 让位动画时长（ms）。180ms 起步快、收尾太硬（观感即"生硬"）；
    /// 260ms 正弦缓出：起步利落、收尾柔和，与盯盘自选股列表（280ms）同一节奏。
    /// </summary>
    private const int GroupFlipMs = 260;

    /// <summary>
    /// 单个分组列表的拖拽上下文。
    /// ★ 分组卡片是 ItemsControl **动态生成**的（每张卡片一个 ListBox），
    ///   故状态必须按 ListBox 实例存放 —— 否则同时拖动两个分组时会互相串味。
    /// </summary>
    private sealed class GroupDragState
    {
        public WatchRow? Row;
        public System.Windows.Controls.ListBoxItem? Container;
        public double StartY;       // 按下点（相对 ListBox）
        public double GrabOffset;   // 按下点相对行顶部的偏移，保持"抓在原处"的手感
        public double PointerY;     // 当前指针 Y，驱动跟手
        public double LastY;        // 上次触发换位的 Y（防抖）
        public string LastTarget = "";
        public bool Active;
        public System.Windows.Media.Effects.Effect? OrigEffect;
        public double OrigOpacity = 1;

        /// <summary>拖拽期间被临时摘掉 Effect 的祖先元素（分组卡片的柔和投影）。</summary>
        public UIElement? EffectOwner;
        public System.Windows.Media.Effects.Effect? SavedEffect;
    }

    private readonly Dictionary<System.Windows.Controls.ListBox, GroupDragState> _groupDrags = new();

    /// <summary>仅当按在行首的「拖拽柄」上才进入拖拽；其余位置保持原有的单击选中 / 双击弹 K 线</summary>
    private void GroupStockList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list) return;
        var st = new GroupDragState();
        _groupDrags[list] = st;

        if (!IsUnderDragHandle(e.OriginalSource as DependencyObject)) return;
        var row = FindDataContext<WatchRow>(e.OriginalSource as DependencyObject);
        if (row == null) return;
        if (list.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.Controls.ListBoxItem container) return;

        st.Row = row;
        st.Container = container;
        st.StartY = e.GetPosition(list).Y;
        st.GrabOffset = e.GetPosition(container).Y;
        st.PointerY = st.StartY;
        st.LastY = st.StartY;
        st.LastTarget = row.Code;

        list.CaptureMouse();   // 拖出列表边界仍能收到 Move / Up
        e.Handled = true;      // 避免拖拽柄按下时把该行选中
    }

    private void GroupStockList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list) return;
        if (!_groupDrags.TryGetValue(list, out var st) || st.Row == null || st.Container == null) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { GroupEndDrag(list, st); return; }

        var y = e.GetPosition(list).Y;
        if (!st.Active)
        {
            if (Math.Abs(y - st.StartY) < 4) return;   // 位移阈值：避免把一次点击误判为拖动
            st.Active = true;
            GroupStartMask(st);
            System.Windows.Controls.Panel.SetZIndex(st.Container, 1);
        }

        st.PointerY = y;
        GroupFollowPointer(list, st);

        if (Math.Abs(y - st.LastY) < 4) return;
        st.LastY = y;

        var target = GroupRowAtY(list, st, y);
        if (!string.IsNullOrEmpty(target) && target != st.Row.Code && target != st.LastTarget)
        {
            GroupReorder(list, st, target);
            st.LastTarget = target;
        }
    }

    private void GroupStockList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list) return;
        if (!_groupDrags.TryGetValue(list, out var st)) return;
        if (st.Row != null) e.Handled = true;   // 拖拽结束时吃掉事件，避免顺带触发行的选中
        GroupEndDrag(list, st);                 // 内部无条件移除字典项（未命中拖拽柄的空状态也一并清掉）
    }

    /// <summary>把被拖拽行移到目标行位置：数据换位（Move 不重建容器）+ 其它行 FLIP 让位</summary>
    private static void GroupReorder(System.Windows.Controls.ListBox list, GroupDragState st, string targetCode)
    {
        if (st.Row == null || list.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<WatchRow> rows) return;
        var target = rows.FirstOrDefault(r => string.Equals(r.Code, targetCode, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;

        var from = rows.IndexOf(st.Row);
        var to = rows.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        var first = GroupCaptureRowY(list, rows, withTransform: true);   // ① First
        rows.Move(from, to);                                             // ② 数据换位
        list.UpdateLayout();                                            // ③ Last
        GroupPlayFlip(list, rows, first, st.Row.Code);                   // ④ Invert + Play
    }

    private static Dictionary<string, double> GroupCaptureRowY(System.Windows.Controls.ListBox list,
        System.Collections.ObjectModel.ObservableCollection<WatchRow> rows, bool withTransform)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            var y = fe.TranslatePoint(new System.Windows.Point(0, 0), list).Y;
            if (!withTransform && fe.RenderTransform is TranslateTransform tt) y -= tt.Y;
            map[rows[i].Code] = y;
        }
        return map;
    }

    /// <summary>
    /// FLIP 的 Invert + Play：把各行瞬时拉回**原来的视觉位置**，再用缓动动画归零。
    ///
    /// ★ 核心：Invert 的位移必须按「**布局位置**」算。
    ///   目标是让行的视觉位置保持在 <paramref name="first"/>（记录时的**视觉** Y，含未播完的临时位移），
    ///   而"换位后的布局 Y"要用**当前视觉 Y 扣掉正在生效的位移**才准确。
    ///   直接拿两个视觉 Y 相减会漏掉在飞的位移 —— 快速拖拽（上一次让位还没播完就再次换位）时，
    ///   每换一位该行就跳一下，这正是"过渡动画生硬"的根源。
    /// </summary>
    private static void GroupPlayFlip(System.Windows.Controls.ListBox list,
        System.Collections.ObjectModel.ObservableCollection<WatchRow> rows,
        Dictionary<string, double> first, string? excludeCode)
    {
        var dur = TimeSpan.FromMilliseconds(GroupFlipMs);
        // EasingFunction 是 Freezable：冻结后可被多个动画安全共享（不冻结则会被标记为"共享"，有隐患）
        var ease = new System.Windows.Media.Animation.SineEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        ease.Freeze();

        for (var i = 0; i < rows.Count; i++)
        {
            var code = rows[i].Code;
            if (code.Equals(excludeCode, StringComparison.OrdinalIgnoreCase)) continue;   // 被拖行自作跟手，不参与补间
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (!first.TryGetValue(code, out var y0)) continue;

            var tt = fe.RenderTransform as TranslateTransform;
            var cur = tt?.Y ?? 0;                                                  // 正在生效的临时位移
            var layoutY = fe.TranslatePoint(new System.Windows.Point(0, 0), list).Y - cur;   // 扣掉位移 = 布局位置
            var delta = y0 - layoutY;                                              // 保持视觉位置不动 → 连续、不跳

            if (Math.Abs(delta) < 0.5)
            {
                // 新槽位与旧视觉位置重合：直接落位即可（无位移可播），但要把残留的动画位清零
                if (tt != null) { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; }
                fe.RenderTransform = null;
                continue;
            }

            if (tt == null) { tt = new TranslateTransform(); fe.RenderTransform = tt; }
            else tt.BeginAnimation(TranslateTransform.YProperty, null);   // 停掉上一次未播完的让位

            tt.Y = delta;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        }
    }

    /// <summary>拖拽中：被拖拽行实时跟随鼠标（直接设位移，不做补间）</summary>
    private static void GroupFollowPointer(System.Windows.Controls.ListBox list, GroupDragState st)
    {
        if (st.Container == null) return;
        var layoutY = st.Container.TranslatePoint(new System.Windows.Point(0, 0), list).Y;

        var tt = st.Container.RenderTransform as TranslateTransform;
        if (tt == null) { tt = new TranslateTransform(); st.Container.RenderTransform = tt; }
        else
        {
            layoutY -= tt.Y;
            // 只在真有残留动画时清一次：每帧都调 BeginAnimation(null) 会不停重建动画时钟，
            // 跟手时反而是个纯开销（被拖行本身不参与让位补间，清一次就够）。
            if (tt.HasAnimatedProperties) tt.BeginAnimation(TranslateTransform.YProperty, null);
        }

        tt.Y = (st.PointerY - st.GrabOffset) - layoutY;
    }

    /// <summary>落点判定：指针落在哪一行的行内（一律用**布局位置**，不受让位动画影响）</summary>
    private static string? GroupRowAtY(System.Windows.Controls.ListBox list, GroupDragState st, double y)
    {
        if (list.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<WatchRow> rows) return null;
        for (var i = 0; i < rows.Count; i++)
        {
            var code = rows[i].Code;
            if (code.Equals(st.Row?.Code, StringComparison.OrdinalIgnoreCase)) continue;
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            var top = fe.TranslatePoint(new System.Windows.Point(0, 0), list).Y;
            if (fe.RenderTransform is TranslateTransform tt) top -= tt.Y;
            if (y >= top && y <= top + fe.ActualHeight) return code;
        }
        return null;
    }

    /// <summary>拖拽视觉：整行半透明 + 轻投影（与盯盘列表一致）</summary>
    private static void GroupStartMask(GroupDragState st)
    {
        if (st.Container == null) return;
        st.OrigEffect = st.Container.Effect;
        if (FindVisualChild<Border>(st.Container) is { } bd)
        {
            st.OrigOpacity = bd.Opacity;
            bd.Opacity = 0.55;
        }
        st.Container.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 10, ShadowDepth = 0, Opacity = 0.30, Color = System.Windows.Media.Colors.Black
        };

        // ★ 拖拽期间摘掉**祖先**（分组卡片）的柔和投影：
        //   带 Effect 的元素会把整棵子树渲染进中间位图再模糊，子树里只要有东西在变，
        //   这一整套每帧都要重做一次 —— 拖拽时行一直在动，于是每帧重渲染整张卡片，观感就是发卡。
        //   卡片投影只有 0.10 透明度，暂时摘掉几乎看不出来；被拖行自己保留轻微投影做"拎起"提示。
        if (FindEffectAncestor(st.Container) is { } owner)
        {
            st.EffectOwner = owner;
            st.SavedEffect = owner.Effect;
            owner.Effect = null;
        }
    }

    private static void GroupClearMask(GroupDragState st)
    {
        if (st.Container == null) return;
        if (FindVisualChild<Border>(st.Container) is { } bd) bd.Opacity = st.OrigOpacity;
        st.Container.Effect = st.OrigEffect;
        st.OrigEffect = null;

        // 还原卡片投影
        if (st.EffectOwner != null)
        {
            st.EffectOwner.Effect = st.SavedEffect;
            st.EffectOwner = null;
            st.SavedEffect = null;
        }
    }

    /// <summary>沿视觉树向上找第一个带 Effect 的祖先元素（用于拖拽期间临时摘掉卡片投影）</summary>
    private static UIElement? FindEffectAncestor(System.Windows.DependencyObject start)
    {
        var cur = System.Windows.Media.VisualTreeHelper.GetParent(start);
        for (var guard = 0; cur != null && guard < 24; guard++)
        {
            if (cur is UIElement { Effect: not null } ue) return ue;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void GroupEndDrag(System.Windows.Controls.ListBox list, GroupDragState st)
    {
        _groupDrags.Remove(list);
        if (st.Container == null) return;

        var container = st.Container;
        try { list.ReleaseMouseCapture(); } catch { }
        try { System.Windows.Controls.Panel.SetZIndex(container, 0); } catch { }
        GroupClearMask(st);

        if (container.RenderTransform is TranslateTransform tt && Math.Abs(tt.Y) > 0.5)
        {
            // 松手后让"被拎起的行"平滑沉回槽位：160ms 太赶、显得急停，220ms 收尾更柔
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            });
        }
        else container.RenderTransform = null;

        if (!st.Active || st.Row == null) return;

        // 拖拽结束后把顺序落盘；并切回「加入顺序」—— 否则下一次行情刷新会按涨跌幅重排，拖拽等于白做
        if (FindDataContext<GroupViewModel>(list) is { } vm)
        {
            vm.ApplyManualOrder();
            QueueAutoSave();
        }
    }

    #endregion

    /// <summary>添加股票（去重后加入股票池）；新加入的股票默认勾选（不在 HiddenStocks 中）</summary>
    private void AddCodes(IEnumerable<string> codes)
    {
        var existing = _settings.Stocks.Select(TencentStockClient.NormalizeCode).ToHashSet();
        var added = 0;
        var addedCodes = new List<string>();
        var rejected = 0;
        foreach (var raw in codes)
        {
            var n = TencentStockClient.NormalizeCode(raw);
            // ★ 只接受合法 A 股代码：否则美股等（szusmpb.oq）会被存进股票池，且永远取不到行情
            if (!TencentStockClient.IsAShareCode(n)) { rejected++; continue; }
            // 追加到末尾 —— 新加入的股票默认排在列表最后（顺序即 _settings.Stocks 的顺序）
            if (existing.Add(n)) { _settings.Stocks.Add(n); addedCodes.Add(n); added++; }
        }
        if (added == 0)
        {
            if (rejected > 0) AppDialog.Info($"只支持 A 股代码（沪/深/北，如 600519、000001、920992），已忽略 {rejected} 项无效输入。", "无效代码");
            return;
        }
        RefreshWatchList();
        RefreshDetailStocks();
        // 选中最后加入的那只（若被板块筛选隐藏则找不到，属正常），便于立刻用「上移 / 下移」调整位置
        var last = _watchRows.LastOrDefault(r => addedCodes.Contains(r.Code));
        if (last != null) StocksList.SelectedItem = last;
        QueueLiveApply();
        QueueAutoSave();
    }

    private void ClearSearchBox()
    {
        _suppressSearchText = true;
        NewStockBox.Clear();
        _suppressSearchText = false;
        SearchPopup.IsOpen = false;
    }

    private void NewStockBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _suppressSearchText) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>执行搜索并弹出候选（请求带序号，过期结果丢弃，避免乱序覆盖）</summary>
    private async System.Threading.Tasks.Task DoSearchAsync()
    {
        var kw = NewStockBox.Text.Trim();
        if (kw.Length == 0)
        {
            SearchPopup.IsOpen = false;
            SearchList.ItemsSource = null;
            return;
        }

        var seq = ++_searchSeq;
        List<StockSearchItem> items;
        try { items = await StockSearchService.SearchAsync(kw); }
        catch { items = new List<StockSearchItem>(); }

        if (seq != _searchSeq) return;
        SearchList.ItemsSource = items;
        SearchPopup.IsOpen = items.Count > 0;
    }

    private void SearchList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is not StockSearchItem item) return;
        AddCodes(new[] { item.Code });
        ClearSearchBox();
    }

    /// <summary>自选股勾选变化：勾选→盯盘显示；取消→盯盘隐藏；实时推送盯盘窗口重建</summary>
    private void StockSel_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.DataContext is not WatchRow row) return;

        var show = cb.IsChecked == true;
        row.IsSelected = show;
        if (show) _settings.HiddenStocks.Remove(row.Code);
        else if (!_settings.HiddenStocks.Contains(row.Code)) _settings.HiddenStocks.Add(row.Code);

        UpdateStockCount();
        QueueLiveApply();
        QueueAutoSave();
    }
    #endregion

    #region 通用设置项
    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.RefreshIntervalMs = Math.Max(1000, (int)Math.Round(RefreshSlider.Value / 1000.0) * 1000);
        RefreshText.Text = _settings.RefreshIntervalMs + " ms";
        QueueLiveApply();
    }

    private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.FontSize = Math.Round(FontSlider.Value);
        FontText.Text = _settings.FontSize.ToString("F0");
        QueueLiveApply();
    }

    private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.BackgroundOpacity = BgOpacitySlider.Value;
        BgOpacityText.Text = _settings.BackgroundOpacity.ToString("F2");
        QueueLiveApply();
    }

    private void HoverChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.HoverToShow = HoverChk.IsChecked == true;
        QueueLiveApply();
    }

    private void TopChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AlwaysOnTop = TopChk.IsChecked == true;
        QueueLiveApply();
    }

    private void TextOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.TextOpacity = TextOpacitySlider.Value;
        TextOpacityText.Text = _settings.TextOpacity.ToString("F2");
        QueueLiveApply();   // 实时作用于盯盘窗口（内容与窗口底色互不影响）
    }

    private void MinimalChk_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.MinimalMode = MinimalChk.IsChecked == true;
        QueueLiveApply();   // 实时重建盯盘窗口（极简/常规两种布局）
        QueueAutoSave();
    }

    private void ThemeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<ThemeMode>(tag, out var mode))
        {
            _settings.Theme = mode;
            ThemeManager.Apply(mode);
            QueueLiveApply();
        }
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (((WButton)sender).Tag is not DisplayField field) return;
        using var dlg = new System.Windows.Forms.ColorDialog();
        try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(field.Color); } catch { }
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        field.Color = "#" + dlg.Color.R.ToString("X2") + dlg.Color.G.ToString("X2") + dlg.Color.B.ToString("X2");
        FieldsGrid.Items.Refresh();
        QueueLiveApply();
    }

    /// <summary>字段显隐勾选：单击即应用（无需进入编辑态）</summary>
    private void FieldChk_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        FieldsGrid.Items.Refresh();
        QueueLiveApply();
    }

    /// <summary>颜色模式切换（固定颜色 / 涨红跌绿）。
    /// TwoWay 绑定已经把值写回 DisplayField.ColorMode，这里只负责即时生效与持久化；
    /// 故意不调用 Items.Refresh()：重建单元格模板会闪一下，而该列自身已显示新值，无刷新必要。</summary>
    private void ColorModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        // 模板生成 / Items.Refresh 时的初始赋值不算用户操作（那时控件尚未加载完成）
        if (sender is not WCombo cb || !cb.IsLoaded) return;
        QueueLiveApply();
    }

    /// <summary>「显示名称」勾选：取消后盯盘界面不显示该列标题，数值并入股价所在行</summary>
    private void FieldLabelChk_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        FieldsGrid.Items.Refresh();
        QueueLiveApply();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        _liveApply.Stop();
        SaveSettings("已保存");
        RaiseSettingsChanged();
    }
    #endregion

    #region 分页 / 自动刷新联动
    private void MainTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 期间本事件会早于部分控件创建触发（届时 Kline/定时器尚未就绪）
        if (_initializing) return;

        // ① 先把各页的自动刷新勾选对齐到"当前停留的页"（离开的页取消，进入的页勾上）
        SyncAutoRefreshByTab();
        // ② 再按新状态启停各页定时器（两处的判定口径必须一致，故紧邻调用）
        UpdateKlineTimer();
        UpdateGroupRefreshTimer();
        UpdateOverviewTimer();
        // 全球市场页：仅主窗可见且停留本页时运行其定时器（离开即停，不在后台空转）
        GlobalView.SetActive(IsVisible && MainTab.SelectedItem == GlobalTab);
        // ③ 进入页面时立即刷新一次，不必空等一个周期
        if (MainTab.SelectedItem == GroupTab) _ = RefreshGroupQuotesAsync();
        if (MainTab.SelectedItem == OverviewTab) _ = Overview.RefreshAsync();
    }

    /// <summary>
    /// 分页切换时的自动刷新联动：**自动刷新只服务于当前可见页**。
    /// 离开的页把勾选取消（其定时器随之停止，不在后台空转占资源），
    /// 进入的页若有自动刷新勾选项则勾上（并立即生效）。
    /// 勾选是"页面级"的 —— 因此三页各用一个独立配置字段，互不干扰。
    /// </summary>
    private void SyncAutoRefreshByTab()
    {
        var onKline = MainTab.SelectedItem == KlineTab;
        var onMulti = MainTab.SelectedItem == MultiKlineTab;
        var onGroup = MainTab.SelectedItem == GroupTab;
        var onOverview = MainTab.SelectedItem == OverviewTab;

        // 置抑制位：避免联动赋值触发各页自己的"用户勾选"处理（会各写一次配置并各自启停一遍）
        _syncingAutoRefresh = true;
        try
        {
            Kline.SetAutoRefreshChecked(onKline);
            MultiKline.SetAutoRefreshChecked(onMulti);
            GroupAutoRefreshChk.IsChecked = onGroup;
            Overview.SetAutoRefreshChecked(onOverview);

            _settings.KlineAutoRefresh = onKline;
            _settings.MultiKlineAutoRefresh = onMulti;
            _settings.GroupAutoRefresh = onGroup;
            _settings.OverviewAutoRefresh = onOverview;
        }
        finally { _syncingAutoRefresh = false; }
    }

    /// <summary>主窗可见且在 K线 / 多股K线 页、且开启自动刷新时激活对应控件的定时器</summary>
    private void UpdateKlineTimer()
    {
        var onKline = MainTab.SelectedItem == KlineTab;
        var onMulti = MainTab.SelectedItem == MultiKlineTab;
        Kline.SetActive(IsVisible && onKline && _settings.KlineAutoRefresh);
        MultiKline.SetActive(IsVisible && onMulti && _settings.MultiKlineAutoRefresh);
    }

    /// <summary>仅当勾选自动刷新、且主窗可见并停留在「大盘概览」页时才运行其定时器（该页取数最重）</summary>
    private void UpdateOverviewTimer()
    {
        if (_initializing) return;
        Overview.SetActive(IsVisible && MainTab.SelectedItem == OverviewTab && _settings.OverviewAutoRefresh);
    }

    /// <summary>分页联动赋值期间置位：各页看到它就不当成"用户手工勾选"（由 <see cref="SyncAutoRefreshByTab"/> 统一处理）</summary>
    private bool _syncingAutoRefresh;
    #endregion

    /// <summary>
    /// 「分组自选」双击某只票：**始终弹出独立的单股 K 线窗口**（与「行情明细」页共用同一 KlineChart，功能完全一致）。
    /// 说明：早期版本在「多股K线模式」开启时会改为把该票叠加到多股K线页并切页，
    /// 用户明确要求双击就是"弹出新K线窗口"，不再跳转，故移除该分支。
    /// 多股K线容器仍可通过其右侧搜索框自行添加。
    /// </summary>
    private void OpenKlineForRow(WatchRow row)
    {
        var w = new KlineWindow(row.Code, row.Name, _settings, () => QueueAutoSave()) { Owner = this };
        w.Show();
    }

    private void DetailStockCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _suppressDetailCombo) return;   // 程序化回填（RefreshDetailStocks）不当作"用户选股"

        if (DetailStockCombo.SelectedItem is WatchRow sel && IsVisible)
            Kline.SetStock(sel.Code, sel.Name);
    }

    /// <summary>程序化改动个股下拉（RefreshDetailStocks 回填）时置位，避免被误判成"用户选股"</summary>
    private bool _suppressDetailCombo;

    /// <summary>仅当勾选自动刷新、且主窗可见并停留在「分组自选」页时，才运行分组行情定时器（离开/隐藏即停）</summary>
    private void UpdateGroupRefreshTimer()
    {
        if (_initializing) return;
        _groupRefresh.IsEnabled = IsVisible && MainTab.SelectedItem == GroupTab && _settings.GroupAutoRefresh;
    }

    #region 分组自选（同花顺式）
    /// <summary>从 settings.Groups 构建分组视图模型；共享 WatchRow 实例，行情刷新就地更新即可同步所有卡片</summary>
    private void BuildGroupViewModels()
    {
        _groupViewModels.Clear();
        _groupRowByCode.Clear();
        foreach (var g in _settings.Groups)
        {
            var vm = new GroupViewModel(g);
            foreach (var code in g.Codes)
            {
                var row = EnsureGroupRow(code);
                if (!vm.Rows.Contains(row)) vm.Rows.Add(row);
            }
            _groupViewModels.Add(vm);
        }
        GroupsPanel.ItemsSource = _groupViewModels;
        _ = RefreshGroupQuotesAsync();
    }

    /// <summary>取（或创建）某代码对应的共享行情行；名称/板块由代码推导，名称稍后由行情填充</summary>
    private WatchRow EnsureGroupRow(string code)
    {
        var n = TencentStockClient.NormalizeCode(code);
        if (_groupRowByCode.TryGetValue(n, out var existing)) return existing;
        var s = StockSector.Classify(n);
        var row = new WatchRow { Code = n, SectorName = s.Name, SectorColor = s.Color, SectorOrder = s.Order };
        _groupRowByCode[n] = row;
        return row;
    }

    /// <summary>拉取所有分组内股票的实时行情（名称/价格/涨跌%）；共享行就地更新，界面自动刷新</summary>
    private async System.Threading.Tasks.Task RefreshGroupQuotesAsync()
    {
        if (_groupRowByCode.Count == 0) return;
        var ok = false;
        try
        {
            var qs = await TencentStockClient.GetQuotesAsync(_groupRowByCode.Keys);
            foreach (var q in qs)
            {
                if (_groupRowByCode.TryGetValue(q.Code, out var row))
                {
                    row.ApplyQuote(q);
                }
            }
            ok = qs.Count > 0;
        }
        catch
        {
            // 行情失败保留上一帧
        }
        // 时间戳便于区分「没刷新」与「刷新了但无数据」
        if (!_initializing) GroupRefreshInfo.Text = (ok ? "已更新 " : "刷新失败 ") + DateTime.Now.ToString("HH:mm:ss");

        // 组内排序是显示层的（不改动持久化的 Codes），行情每次刷新后必须重新应用一次，
        // 否则新价格进来后顺序就不再符合涨跌幅。
        foreach (var vm in _groupViewModels) vm.ApplySort();
    }

    /// <summary>手动刷新一次分组内股价</summary>
    private void GroupRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _ = RefreshGroupQuotesAsync();
    }

    /// <summary>勾选/取消自动刷新：写回配置并即时启停定时器；勾选后先立即刷新一次，避免空等一个周期</summary>
    private void GroupAutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        // 分页联动期间由 SyncAutoRefreshByTab 统一写配置/刷新，这里不再重复（否则切页会触发两次刷新）
        if (_syncingAutoRefresh) return;
        _settings.GroupAutoRefresh = GroupAutoRefreshChk.IsChecked == true;
        QueueAutoSave();
        UpdateGroupRefreshTimer();
        if (_settings.GroupAutoRefresh) _ = RefreshGroupQuotesAsync();
    }

    /// <summary>把股票加入某分组（去重 + 单组 ≤100）；同时维护共享行与持久化代码</summary>
    private void AddCodeToGroup(GroupViewModel g, string rawCode)
    {
        var code = TencentStockClient.NormalizeCode(rawCode);
        if (string.IsNullOrWhiteSpace(code)) return;
        if (g.Model.Codes.Count >= StockGroup.MaxItems) return;   // 已满，跳过
        if (g.Model.Codes.Contains(code)) return;                // 已存在，跳过

        var row = EnsureGroupRow(code);
        g.Model.Codes.Add(code);
        if (!g.Rows.Contains(row)) g.Rows.Add(row);
        QueueAutoSave();
        _ = RefreshGroupQuotesAsync();
    }

    /// <summary>把股票从某分组移除；若已无任何分组引用该代码，回收共享行</summary>
    private void RemoveCodeFromGroup(GroupViewModel g, WatchRow row)
    {
        g.Model.Codes.Remove(row.Code);
        g.Rows.Remove(row);
        ReapGroupRows();
        QueueAutoSave();
    }

    /// <summary>回收不再被任何分组引用的共享行情行（避免字典无限增长）</summary>
    private void ReapGroupRows()
    {
        var live = new HashSet<string>(_settings.Groups.SelectMany(g => g.Codes), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _groupRowByCode.Keys.ToList())
            if (!live.Contains(key)) _groupRowByCode.Remove(key);
    }

    private GroupViewModel NewGroupInternal(string? name = null)
    {
        var g = new StockGroup { Name = name ?? ("新分组 " + (_settings.Groups.Count + 1)) };
        _settings.Groups.Add(g);
        var vm = new GroupViewModel(g);
        _groupViewModels.Add(vm);
        QueueAutoSave();
        return vm;
    }

    private void NewGroupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        NewGroupInternal();
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not GroupViewModel g) return;
        if (!AppDialog.Confirm($"删除分组「{g.Name}」？分组内的股票不会影响到其它分组。", "删除分组")) return;

        _settings.Groups.Remove(g.Model);
        _groupViewModels.Remove(g);
        if (_selectedGroup == g) { _selectedGroup = null; _selectedRow = null; }
        ReapGroupRows();
        QueueAutoSave();
    }

    /// <summary>
    /// 切换该分组的组内排序：涨幅优先 → 跌幅优先 → 加入顺序（只影响显示顺序，不改持久化的 Codes）。
    /// 图标与悬停提示由 VM 的 SortIcon / SortHint 驱动并绑定在 XAML 上 ——
    /// **这里不要再给 b.ToolTip 赋值**：本地值会覆盖 Binding，导致之后再切换时提示不再更新。
    /// </summary>
    private void SortGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not GroupViewModel g) return;
        g.ToggleSort();
    }

    /// <summary>分组卡片前移一位</summary>
    private void MoveGroupPrev_Click(object sender, RoutedEventArgs e) => MoveGroupBy(sender, -1);

    /// <summary>分组卡片后移一位</summary>
    private void MoveGroupNext_Click(object sender, RoutedEventArgs e) => MoveGroupBy(sender, +1);

    /// <summary>
    /// 调整分组在列表中的前后位置：同步修改 settings.Groups 与视图模型集合的顺序
    /// （GroupsPanel 直接绑定 _groupViewModels，用 ObservableCollection.Move 即可，无需整表重建）。
    /// </summary>
    private void MoveGroupBy(object sender, int step)
    {
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not GroupViewModel g) return;

        var idx = _settings.Groups.IndexOf(g.Model);
        var target = idx + step;
        if (idx < 0 || target < 0 || target >= _settings.Groups.Count) return;

        _settings.Groups.RemoveAt(idx);
        _settings.Groups.Insert(target, g.Model);
        _groupViewModels.Move(idx, target);
        QueueAutoSave();
    }

    private void GroupName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is System.Windows.Controls.TextBox tb && tb.DataContext is GroupViewModel g)
        {
            if (string.IsNullOrWhiteSpace(tb.Text)) { tb.Text = g.Name; return; }
            g.Name = tb.Text.Trim();
            QueueAutoSave();
        }
    }

    private void GroupRow_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (sender is System.Windows.Controls.ListBox lb && lb.DataContext is GroupViewModel g)
        {
            _selectedGroup = g;
            _selectedRow = lb.SelectedItem as WatchRow;
        }
    }

    private void GroupRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_initializing) return;
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem is WatchRow row) OpenKlineForRow(row);
    }

    private void GroupRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not WatchRow row) return;
        // 行对象跨分组共享（EnsureGroupRow 同一代码同一行实例），必须定位到「点击所在分组」的宿主列表，
        // 而不是"第一个包含该行的分组"——否则同票在多个分组时会删错分组。
        // 沿视觉树向上找，第一个 DataContext 为 GroupViewModel 的祖先就是宿主卡片（ListBox 与卡片根共用该 VM）。
        var owner = FindDataContext<GroupViewModel>(b);
        if (owner != null && owner.Rows.Contains(row)) { RemoveCodeFromGroup(owner, row); return; }

        // 兜底：视觉树拿不到宿主时按旧逻辑处理
        foreach (var g in _groupViewModels)
        {
            if (g.Rows.Contains(row)) { RemoveCodeFromGroup(g, row); break; }
        }
    }

    private void DeleteSelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (_selectedGroup != null && _selectedRow != null) RemoveCodeFromGroup(_selectedGroup, _selectedRow);
        _selectedGroup = null;
        _selectedRow = null;
    }

    // ===== 「添加到哪些分组」弹窗（支持一次加入多个分组）=====
    private void GroupSearchBox_StockPicked(object? sender, StockSearchItem item)
    {
        if (_initializing) return;
        OpenAddToGroupPopup(item);
    }

    private void AddToGroupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var text = GroupSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = TryResolveAndOpenAsync(text);
    }

    private async System.Threading.Tasks.Task TryResolveAndOpenAsync(string text)
    {
        var item = await ResolveSearchItemAsync(text);
        if (item != null) OpenAddToGroupPopup(item);
    }

    private async System.Threading.Tasks.Task<StockSearchItem?> ResolveSearchItemAsync(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        // 形如 600519 / sh600519 / sz000001 视为代码
        var digits = t.StartsWith("sh", StringComparison.OrdinalIgnoreCase) || t.StartsWith("sz", StringComparison.OrdinalIgnoreCase)
            ? t.Substring(2) : t;
        if (digits.Length > 0 && digits.All(char.IsDigit))
            return new StockSearchItem { Code = TencentStockClient.NormalizeCode(t), Name = TencentStockClient.NormalizeCode(t) };
        try
        {
            var items = await StockSearchService.SearchAsync(t);
            return items.Count > 0 ? items[0] : null;
        }
        catch { return null; }
    }

    private void OpenAddToGroupPopup(StockSearchItem item)
    {
        _pendingAddItem = item;
        // 至少保留一个分组可添加
        if (_groupViewModels.Count == 0) NewGroupInternal();

        // 已包含此股票的分组：显示为「已勾选 + 禁用」，从源头阻断重复添加
        // （AddCodeToGroup 本身也会去重，但那只是兜底，界面上必须一眼看出"这个组已经有了"）
        var code = TencentStockClient.NormalizeCode(item.Code);
        AddTargetList.ItemsSource = _groupViewModels
            .Select(g =>
            {
                var exists = g.Model.Codes.Contains(code);
                return new AddTargetItem { Group = g, AlreadyInGroup = exists, IsChecked = exists };
            })
            .ToList();
        AddNewGroupChk.IsChecked = false;
        AddToGroupHint.Text = $"将添加：{item.Name}  {item.Code}";
        AddToGroupPopup.IsOpen = true;
    }

    private void AddConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing || _pendingAddItem == null) { AddToGroupPopup.IsOpen = false; return; }
        var code = TencentStockClient.NormalizeCode(_pendingAddItem.Code);

        var targets = (AddTargetList.ItemsSource as List<AddTargetItem>) ?? new List<AddTargetItem>();
        // CanCheck 过滤掉「已包含」的行 —— 它们虽然显示为已勾选，但不参与本次添加
        foreach (var t in targets.Where(t => t.IsChecked && t.CanCheck)) AddCodeToGroup(t.Group, code);

        if (AddNewGroupChk.IsChecked == true)
        {
            var ng = NewGroupInternal();
            AddCodeToGroup(ng, code);
        }

        AddToGroupPopup.IsOpen = false;
        _pendingAddItem = null;
    }

    #region 分组导入 / 导出（.sl 文件）

    /// <summary>分组文件扩展名（.sl = StockLooking）与文件筛选串</summary>
    private const string GroupFileExt = ".sl";
    private const string GroupFileFilter = "StockLooking 分组文件 (*.sl)|*.sl|所有文件 (*.*)|*.*";

    /// <summary>导出单个分组（分组卡片右上角的图标按钮）到用户自选位置的 .sl 文件</summary>
    private void ExportSingleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.DataContext is not GroupViewModel vm) return;

        var group = vm.Model;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"导出分组：{group.Name}",
            Filter = GroupFileFilter,
            DefaultExt = GroupFileExt,
            AddExtension = true,
            // 文件名用分组名（非法字符替换掉），便于识别
            FileName = $"StockLooking_{SanitizeFileName(group.Name)}_{DateTime.Now:yyyyMMdd}"
        };
        if (dlg.ShowDialog(this) != true) return;   // 保存位置由用户决定

        try
        {
            // 单个分组也序列化成「分组数组」，与导入端的 List<StockGroup> 解析保持一致
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { group }, GroupFileJsonOptions());
            System.IO.File.WriteAllText(dlg.FileName, json);
            AppDialog.Info($"已导出分组「{group.Name}」（{group.Codes.Count} 只）。\n{dlg.FileName}", "导出分组");
        }
        catch (Exception ex)
        {
            AppDialog.Error("导出失败：" + ex.Message, "导出分组");
        }
    }

    /// <summary>把分组名转成合法文件名（替换路径分隔符等非法字符）</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "分组" : s;
    }

    /// <summary>
    /// 从 .sl 文件导入分组：逐个弹出重命名对话框（可保持原名），
    /// 若与现有分组重名则**必须修改**后才能导入；在对话框里点「取消」则跳过该分组。
    /// </summary>
    private void ImportGroups_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入分组",
            Filter = GroupFileFilter,
            DefaultExt = GroupFileExt,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != true) return;

        List<StockGroup> loaded;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            loaded = System.Text.Json.JsonSerializer.Deserialize<List<StockGroup>>(json) ?? new List<StockGroup>();
        }
        catch (Exception ex)
        {
            AppDialog.Error("读取失败（可能不是有效的 .sl 分组文件）：" + ex.Message, "导入分组");
            return;
        }

        if (loaded.Count == 0)
        {
            AppDialog.Info("文件中没有分组。", "导入分组");
            return;
        }

        var added = 0;
        foreach (var g in loaded)
        {
            if (g == null) continue;

            // 已存在的名称：每次都要重新取（前面导入成功的分组也算进去）
            var existing = _settings.Groups.Select(x => x.Name).ToList();
            var nameDlg = new GroupNameDialog(
                string.IsNullOrWhiteSpace(g.Name) ? "新分组" : g.Name,
                $"即将导入分组「{g.Name}」（{g.Codes?.Count ?? 0} 只）。可以保持原名；若名称已存在则必须修改。",
                existing);

            if (nameDlg.ShowDialog() != true) continue;   // 取消 → 跳过该分组

            _settings.Groups.Add(new StockGroup
            {
                Name = nameDlg.GroupName,
                // 与保存时同样的兜底：规范化 + 去重 + 单组上限
                Codes = (g.Codes ?? new List<string>())
                    .Select(TencentStockClient.NormalizeCode)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct()
                    .Take(StockGroup.MaxItems)
                    .ToList()
            });
            added++;
        }

        if (added == 0) return;
        BuildGroupViewModels();
        QueueAutoSave();
        AppDialog.Info($"已导入 {added} 个分组。", "导入分组");
    }

    /// <summary>.sl 文件的序列化选项：易读缩进 + 不转义中文</summary>
    private static System.Text.Json.JsonSerializerOptions GroupFileJsonOptions() =>
        new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

    #endregion                 // ← 关闭「分组导入 / 导出」
    #endregion                 // ← 关闭「分组自选（同花顺式）」

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // 关闭按钮仅隐藏窗口，保留托盘与盯盘窗口；同时停止 K线 / 分组行情 定时器
        if (_allowClose) return;
        Kline.SetActive(false);
        MultiKline.SetActive(false);
        _groupRefresh.Stop();
        e.Cancel = true;
        Hide();
    }

    /// <summary>退出应用时真正关闭窗口</summary>
    public void ForceClose()
    {
        FlushAutoSave();
        _liveApply.Stop();
        _autoSave.Stop();
        _groupRefresh.Stop();
        _allowClose = true;
        Close();
    }

    /// <summary>标题栏按住左键拖拽移动窗口</summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
