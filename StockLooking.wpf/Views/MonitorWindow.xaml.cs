using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;

namespace StockLooking.wpf.Views;

/// <summary>
/// 盯盘浮窗：无边框透明置顶，按配置动态显示字段（名称/股价等均可在设置中单独显隐），
/// 涨红跌绿，快速涨跌显示箭头；数据超出范围时自动调整窗口尺寸；左键任意位置可拖拽。
/// </summary>
public partial class MonitorWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private AppSettings _settings = new();
    /// <summary>
    /// 行情刷新定时器。
    /// ⚠️ 显式用 <see cref="DispatcherPriority.Normal"/>：默认的 Background 优先级会被连续的 UI 工作
    /// （K 线重绘、大批量请求回填等）**长期排队延后**，表现就是"主程序一忙，盯盘价格就卡住"。
    /// </summary>
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);
    /// <summary>悬停模式下轮询全局鼠标位置：隐藏的窗口收不到 MouseEnter，只能主动探测</summary>
    private readonly DispatcherTimer _hoverWatch = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<string, StockCard> _cards = new();
    private readonly Dictionary<string, decimal?> _prevPrices = new();
    private bool _refreshing;
    /// <summary>拖拽进行中：期间暂停悬停隐藏，避免误把正在拖动的窗口藏掉</summary>
    private bool _dragging;
    /// <summary>首次数据到达后按真实内容精调一次窗口尺寸（之后尊重用户手动缩放）</summary>
    private bool _needFit = true;
    /// <summary>首次显示时把窗口贴到屏幕工作区右下角（尺寸定稿后停止自动定位）</summary>
    private bool _needPlace = true;
    /// <summary>手动拖动窗口中（用 Left/Top 实现，不走 DragMove）</summary>
    private bool _windowDragging;
    /// <summary>拖动开始时的窗口位置与屏幕光标位置（用于绝对定位，避免累积误差）</summary>
    private double _dragOriginLeft, _dragOriginTop, _dragStartScreenX, _dragStartScreenY;
    /// <summary>上一次的极简模式取值，用于识别"模式互切"（切换后需要重新定位）</summary>
    private bool? _lastMinimalMode;

    private class StockCard
    {
        /// <summary>卡片根元素：拖拽排序时用于定位与重排子元素顺序</summary>
        public Border? Root;
        public TextBlock? NameTb;
        public TextBlock? PriceTb;
        public TextBlock? ArrowTb;
        public TextBlock? ChangeTb;
        public readonly Dictionary<string, TextBlock> Chips = new();
    }

    /// <summary>正在拖拽排序的股票代码（为 null 表示未在排序拖拽中）</summary>
    private string? _dragCode;
    /// <summary>上一次换位的目标卡片（防止在同一目标上反复触发换位，导致动画连环重启而抖动）</summary>
    private string? _lastDropTarget;
    /// <summary>上一次判定用的鼠标 Y（做位移阈值，避免抖动频繁换位）</summary>
    private double _dragLastY;
    /// <summary>按下手柄时，光标相对卡片顶部的偏移 —— 让卡片跟着鼠标走时保持"抓在原处"的手感</summary>
    private double _dragGrabOffset;
    /// <summary>拖拽中光标的纵向位置（相对 StockPanel），用于驱动卡片实时跟随</summary>
    private double _dragPointerY;
    /// <summary>加蒙版前卡片原本的底色与效果（松手时原样恢复）</summary>
    private System.Windows.Media.Brush? _dragOrigBackground;
    private System.Windows.Media.Effects.Effect? _dragOrigEffect;

    /// <summary>
    /// 盯盘窗口内拖拽调整顺序后触发：顺序已写入 <see cref="AppSettings.Stocks"/>，
    /// 由 App 负责持久化并通知主窗口刷新列表（保证两处顺序一致）。
    /// </summary>
    public event EventHandler? OrderChanged;

    /// <summary>
    /// 窗口可见性变化（双击隐藏、悬停自动隐藏、托盘切换等都会走到）时触发，
    /// 供 App 同步托盘菜单「显示盯盘窗口」的勾选态 —— 否则菜单会与实际状态不一致。
    /// （UIElement 的 IsVisibleChanged 是事件而非虚方法，故在构造函数里订阅。）
    /// </summary>
    public event EventHandler<bool>? VisibilityChanged;

    public MonitorWindow(AppSettings settings)
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _hoverWatch.Tick += (_, _) => HoverWatchTick();
        IsVisibleChanged += (_, _) =>
        {
            // 可见性变化 → 通知外部同步托盘菜单勾选态
            VisibilityChanged?.Invoke(this, IsVisible);
            // 重新显示时立刻取一次最新价（隐藏期间不刷新，见 RefreshAsync）
            if (IsVisible) _ = RefreshAsync();
        };
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;

        // 极简 ↔ 常规互切时窗口尺寸会突变，重新贴到屏幕右下角，避免放大后跑出可视区
        if (_lastMinimalMode != settings.MinimalMode)
        {
            _lastMinimalMode = settings.MinimalMode;
            _needPlace = true;
        }

        Topmost = _settings.AlwaysOnTop;
        // 极简模式：窗口底色完全透明，让股价/涨跌幅直接"浮"在桌面上。
        // ⚠️ 必须用 Brushes.Transparent 而不是 null：Background 为 null 的元素不参与命中测试，
        //    会导致窗口"看得见、点不动"（无法拖动、hover 也无反应）。Transparent 同样是全透明，但可命中。
        RootBorder.Background = _settings.MinimalMode
            ? System.Windows.Media.Brushes.Transparent
            : new SolidColorBrush(ColorFromHex(_settings.BackgroundColor))
            {
                Opacity = _settings.BackgroundOpacity
            };

        // 极简模式：内边距压到最小（更紧凑），且**必须禁用缩放**。
        // 原因：CanResize 时窗口四周有 resize 热区，而极简模式窗口只有一两行高，
        // 上下热区几乎覆盖整个客户区 —— 点在热区上不会触发客户区的拖动事件，表现为"窗口拖不动"。
        RootBorder.Padding = _settings.MinimalMode ? new Thickness(2) : new Thickness(10);
        ResizeMode = _settings.MinimalMode ? ResizeMode.NoResize : ResizeMode.CanResize;
        // 极简模式启用全窗口透明遮罩：保证任意位置都能命中（否则"拖窗口"会失效）；
        // 常规模式收起，以免遮罩挡住卡片上的拖拽排序手柄。
        DragLayer.Visibility = _settings.MinimalMode ? Visibility.Visible : Visibility.Collapsed;
        // 字号：窗口级 + 根容器双重设置，确保所有子元素都能继承到
        FontSize = _settings.FontSize;
        RootBorder.SetValue(TextElement.FontSizeProperty, _settings.FontSize);
        RootBorder.SetValue(TextElement.ForegroundProperty, new SolidColorBrush(ColorFromHex(_settings.ForegroundColor)));
        // 字体透明度：只作用于窗口内的内容（文字、涨跌红绿、拖拽手柄），
        // 与窗口底色（BackgroundOpacity）互不影响 —— 两者可分别调节。
        StockPanel.Opacity = _settings.TextOpacity;

        // ★ 只在"会影响卡片结构"的输入变化时才重建 UI。
        //   原来无条件 BuildUI()，而设置页拖任何滑块都会经由 250ms 的 live-apply 触发本方法 →
        //   盯盘浮窗被每秒重建 4 次（白耗 CPU，也表现为"被主程序拖住"）。
        //   纯外观项（前景色 / 字体透明度 / 背景色）已在上面用属性赋值即时生效，无需重建。
        var sig = UiSignature(settings);
        if (sig != _uiSignature)
        {
            _uiSignature = sig;
            BuildUI();
        }

        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _settings.RefreshIntervalMs));
        if (!_timer.IsEnabled) _timer.Start();

        // 悬停模式开关切换
        if (_settings.HoverToShow)
        {
            if (!_hoverWatch.IsEnabled) _hoverWatch.Start();
        }
        else
        {
            _hoverWatch.Stop();
            // 非悬停模式下也不自动显示：盯盘窗口显隐完全由托盘右键菜单 / 用户显式操作控制，
            // 避免主界面改设置时隐藏的盯盘窗口自动弹出。
        }
    }

    /// <summary>上一次构建 UI 时的结构签名（用于跳过无意义的重建）</summary>
    private string _uiSignature = "";

    /// <summary>
    /// 影响卡片**结构**的输入签名：模式 / 字号 / 股票集合与勾选 / 字段显隐与配色。
    /// 刻意**不含**前景色、字体透明度、背景色等纯外观项 —— 那些用属性赋值即时生效，重建纯属浪费。
    /// </summary>
    private static string UiSignature(AppSettings s)
    {
        var stocks = string.Join(",", s.Stocks.Select(TencentStockClient.NormalizeCode));
        var hidden = string.Join(",", s.HiddenStocks ?? new List<string>());
        var fields = string.Join(",", (s.Fields ?? new List<DisplayField>())
            .Select(f => $"{f.Key}:{(f.IsEnabled ? 1 : 0)}:{(f.ShowLabel ? 1 : 0)}:{(int)f.ColorMode}:{f.Color}:{(f.ShowLabel ? 1 : 0)}"));
        return $"{(s.MinimalMode ? 1 : 0)}|{s.FontSize}|{stocks}|{hidden}|{fields}";
    }

    private bool FieldOn(string key) => _settings.Fields.FirstOrDefault(f => f.Key == key)?.IsEnabled ?? false;

    /// <summary>盯盘界面实际显示的股票：股票池剔除主窗口未勾选（HiddenStocks）的项</summary>
    private List<string> VisibleStocks() =>
        _settings.Stocks
            .Select(TencentStockClient.NormalizeCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .Where(c => !_settings.HiddenStocks.Contains(c))
            .ToList();

    private void BuildUI()
    {
        StockPanel.Children.Clear();
        _cards.Clear();
        // 重建卡片后价格基线失效，重置以免误报「快速涨跌」箭头
        _prevPrices.Clear();
        _needFit = true;

        var visible = VisibleStocks();
        if (visible.Count == 0)
        {
            StockPanel.Children.Add(new TextBlock
            {
                Text = _settings.Stocks.Count == 0
                    ? "未配置自选股\n右键托盘图标 → 打开主窗口 添加"
                    : "未勾选任何自选股\n右键托盘图标 → 打开主窗口 勾选",
                Foreground = new SolidColorBrush(ColorFromHex(_settings.ForegroundColor)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(12)
            });
            AutoFit();
            return;
        }

        foreach (var code in visible)
        {
            var ncode = TencentStockClient.NormalizeCode(code);
            if (string.IsNullOrWhiteSpace(ncode) || _cards.ContainsKey(ncode)) continue;

            var card = new StockCard();
            var minimal = _settings.MinimalMode;   // 极简模式：只留股价+涨跌幅、卡片无背景、行距 2px
            var baseB = ThemeBrush("CardBgBrush") as SolidColorBrush;
            var bc = baseB?.Color ?? Colors.White;
            var border = new Border
            {
                CornerRadius = new CornerRadius(minimal ? 0 : 6),
                Margin = new Thickness(0, 0, 0, minimal ? 2 : 6),   // 项与项之间上下间距：极简 2px
                Padding = minimal ? new Thickness(2, 0, 2, 0) : new Thickness(6),   // 极简模式上下内边距为 0（行更紧凑）
                // 极简模式不画任何背景：文字直接"贴"在桌面上，完全融入。
                // ⚠️ 用 Brushes.Transparent 而非 null —— null 会让该元素失去命中测试，手柄就点不到了。
                Background = minimal ? System.Windows.Media.Brushes.Transparent : new SolidColorBrush(bc) { Opacity = 0.06 }
            };
            var sp = new StackPanel();

            // 标题行：名称/股价/箭头/涨跌幅，以及「不显示标题」的字段（数值直接并入本行）
            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            // 极简模式字体：等宽 + 常规字重（常规模式加粗）+ 字号略大；挂在内容容器上，整行数值统一继承
            if (minimal)
            {
                sp.SetValue(TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei"));
                sp.SetValue(TextElement.FontSizeProperty, _settings.FontSize + 2);
            }
            var weight = minimal ? FontWeights.Normal : FontWeights.Bold;

            // 名称：极简模式强制不显示（等效于在设置里取消勾选「名称」）
            if (!minimal && FieldOn(FieldKeys.Name))
            {
                card.NameTb = new TextBlock { FontWeight = weight, Margin = new Thickness(0, 0, 8, 0) };
                header.Children.Add(card.NameTb);
            }
            if (FieldOn(FieldKeys.Price))
            {
                card.PriceTb = new TextBlock { FontWeight = weight, Margin = new Thickness(0, 0, minimal ? 10 : 4, 0) };
                card.ArrowTb = new TextBlock { FontWeight = weight, Margin = new Thickness(0, 0, 4, 0) };
                header.Children.Add(card.PriceTb);
                header.Children.Add(card.ArrowTb);
            }
            if (FieldOn(FieldKeys.ChangePercent))
            {
                card.ChangeTb = new TextBlock { FontWeight = weight, Margin = new Thickness(0, 0, 8, 0) };
                header.Children.Add(card.ChangeTb);
            }

            // 其余字段：数值并入本行。
            // 极简模式下**一律并入**（即字段名全部不显示）；但"是否显示这个字段"仍然完全遵循
            // 设置页「显示字段与颜色」里的勾选 —— 用户可以自己决定极简模式下要看哪些内容。
            foreach (var f in _settings.Fields.Where(f => f.IsEnabled
                         && f.Key != FieldKeys.Name
                         && f.Key != FieldKeys.Price
                         && f.Key != FieldKeys.ChangePercent
                         && (minimal || !f.ShowLabel)))
            {
                var val = new TextBlock { Margin = new Thickness(0, 0, 10, 0) };
                header.Children.Add(val);
                card.Chips[f.Key] = val;
            }
            sp.Children.Add(header);

            // 显示标题的字段：标题 + 数值纵排，换行排列（极简模式下全部跳过 —— 不显示任何字段名）
            if (!minimal)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var f in _settings.Fields.Where(f => f.IsEnabled
                             && f.Key != FieldKeys.Name
                             && f.Key != FieldKeys.Price
                             && f.Key != FieldKeys.ChangePercent
                             && f.ShowLabel))
                {
                    var chip = new StackPanel { Margin = new Thickness(0, 0, 12, 0), Width = 88 };
                    chip.Children.Add(new TextBlock
                    {
                        Text = f.Label,
                        FontSize = _settings.FontSize * 0.78,
                        Opacity = 0.7
                    });
                    var val = new TextBlock { Text = "-" };
                    chip.Children.Add(val);
                    wrap.Children.Add(chip);
                    card.Chips[f.Key] = val;
                }
                if (wrap.Children.Count > 0) sp.Children.Add(wrap);
            }

            // 内容外套一层 Grid，把拖拽手柄浮在卡片右侧（浮层不参与布局，不会挤动字段排列）。
            // 极简模式**不加手柄**：该模式窗口很窄，24px 的手柄占比过大，很容易被误抓成"排序"而拖不动窗口。
            var layer = new Grid();
            layer.Children.Add(sp);
            if (!minimal) layer.Children.Add(BuildDragHandle(ncode));
            border.Child = layer;
            card.Root = border;
            StockPanel.Children.Add(border);
            _cards[ncode] = card;
        }

        AutoFit();
    }

    /// <summary>
    /// 卡片右侧上下居中的拖拽手柄：图标为「上箭头 + 中间横线 + 下箭头」（表示可上下拖动排序）。
    /// 默认完全透明（**保留命中测试**，鼠标移到该位置才显现），按住左键上下拖动即可调整顺序。
    /// </summary>
    private FrameworkElement BuildDragHandle(string code)
    {
        var handle = new Border
        {
            Tag = "DragHandle",                  // 供窗口级 PreviewMouseLeftButtonDown 识别，避免触发整窗拖动
            Width = 24,
            Height = 20,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,   // 透明但必须有背景才能命中
            Cursor = System.Windows.Input.Cursors.Hand,   // 鼠标移到手柄上时显示"可抓取"的手型
            ToolTip = "按住上下拖动可调整顺序",
            Opacity = 0,
            Child = new System.Windows.Shapes.Path
            {
                // 上人字 + 中间横线 + 下人字；箭头与横线之间留出足够空隙（否则挤成一团看不清）
                Data = System.Windows.Media.Geometry.Parse(
                    "M4,4.4 L7.5,1 L11,4.4 M2.2,8 H12.8 M4,11.6 L7.5,15 L11,11.6"),
                Stroke = new SolidColorBrush(ColorFromHex(_settings.ForegroundColor)),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };

        handle.MouseEnter += (_, _) => handle.Opacity = 1;
        handle.MouseLeave += (_, _) => { if (_dragCode == null) handle.Opacity = 0; };

        handle.MouseLeftButtonDown += (_, e) =>
        {
            _dragCode = code;
            _lastDropTarget = code;      // 起点即"上一次目标"，避免刚按下就触发一次无效换位
            _dragLastY = e.GetPosition(this).Y;
            _dragPointerY = e.GetPosition(StockPanel).Y;
            // 记录"抓取点"相对卡片顶部的偏移，跟随鼠标时保持原处手感；提到最上层并加背景蒙版
            if (_cards.TryGetValue(code, out var self) && self.Root != null)
            {
                _dragGrabOffset = e.GetPosition(self.Root).Y;
                System.Windows.Controls.Panel.SetZIndex(self.Root, 1);
                ApplyDragMask(self.Root);
            }
            handle.Opacity = 1;
            _dragging = true;   // 暂停「鼠标移出自动隐藏」，避免拖到窗口边缘时窗口被藏掉
            handle.CaptureMouse();
            e.Handled = true;   // 关键：吃掉左键，否则窗口级 DragMove 会把排序拖拽变成"拖整个窗口"
        };

        handle.MouseMove += (_, e) =>
        {
            if (_dragCode == null) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { EndDragSort(handle); return; }
            var y = e.GetPosition(this).Y;
            if (Math.Abs(y - _dragLastY) < 4) return;   // 位移阈值：4px（太小易抖、太大则"拖到位了却不换"）
            _dragLastY = y;
            _dragPointerY = e.GetPosition(StockPanel).Y;
            FollowPointer();                            // 被拖拽的卡片实时跟随鼠标（无动画，消除"硬跳"）

            var target = CardCodeAtY(y);
            // 只在「进入另一张卡片」且「与上一次目标不同」时才换位：
            // 否则鼠标在卡片边界附近来回微动会反复触发换位，动画连环重启动 —— 这正是"生硬/抖动"的主因。
            if (!string.IsNullOrEmpty(target) && target != _dragCode && target != _lastDropTarget)
            {
                ReorderByDrag(target!);
                _lastDropTarget = target;
            }
        };

        handle.MouseLeftButtonUp += (_, e) => { EndDragSort(handle); e.Handled = true; };

        return handle;
    }

    /// <summary>
    /// 按窗口内 Y 坐标定位落在哪个"槽位"（拖拽落点判定）。两个要点：
    /// ① **必须排除正在被拖拽的那张** —— 它跟着鼠标走、位置正好盖住落点，
    ///    不排除的话判定会频繁命中"它自己"，于是明明拖到位了却不换位（这是切换不灵敏的主因）；
    /// ② **用布局位置**（扣掉让位动画的临时位移）—— 否则其余卡片正在滑动时判定会忽前忽后。
    /// </summary>
    private string? CardCodeAtY(double y)
    {
        foreach (var kv in _cards)
        {
            if (kv.Key == _dragCode) continue;
            var root = kv.Value.Root;
            if (root == null) continue;
            var top = root.TranslatePoint(new System.Windows.Point(0, 0), this).Y;
            if (root.RenderTransform is TranslateTransform tt) top -= tt.Y;   // 还原为布局位置
            if (y >= top && y <= top + root.ActualHeight) return kv.Key;
        }
        return null;
    }

    /// <summary>
    /// 拖拽过程中实时把当前项移到目标项的位置：
    /// **只调整 StockPanel 的子元素顺序、不重建 UI** —— 重建会销毁正在捕获鼠标的手柄，导致拖拽中断。
    /// 同时同步写入 _settings.Stocks，使显示顺序与数据顺序一致，并给其余卡片加换位过渡动画。
    /// </summary>
    private void ReorderByDrag(string target)
    {
        if (_dragCode == null) return;
        if (!_cards.TryGetValue(_dragCode, out var src) || src.Root == null) return;
        if (!_cards.TryGetValue(target, out var dst) || dst.Root == null) return;

        var fromIdx = StockPanel.Children.IndexOf(src.Root);
        var toIdx = StockPanel.Children.IndexOf(dst.Root);
        if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;

        // ── FLIP · First：记录换位前的**视觉位置**（含上一次动画的临时位移）──
        StockPanel.UpdateLayout();
        var visualBefore = CaptureCardOffsets(withTransform: true);

        StockPanel.Children.RemoveAt(fromIdx);
        StockPanel.Children.Insert(toIdx, src.Root);

        var list = _settings.Stocks;
        var realFrom = list.IndexOf(_dragCode);
        var realTo = list.IndexOf(target);
        if (realFrom >= 0 && realTo >= 0 && realFrom != realTo)
        {
            var c = list[realFrom];
            list.RemoveAt(realFrom);
            list.Insert(realTo, c);
        }

        // ── FLIP · Last + Invert + Play：拿到新布局，再把卡片从"原视觉位置"补间滑过去 ──
        StockPanel.UpdateLayout();
        FollowPointer();      // 顺序已变 → 按新布局重算被拖拽卡片的跟随偏移，否则它会视觉跳一下
        var layoutAfter = CaptureCardOffsets(withTransform: false);
        PlayReorderAnimation(visualBefore, layoutAfter, _dragCode);
    }

    /// <summary>
    /// 记录每张卡片的纵向位置。
    /// <paramref name="withTransform"/> = true 取「视觉位置」（含动画临时位移）；
    /// = false 取「布局位置」（扣掉临时位移）—— FLIP 的 First 用前者、Last 用后者，混用会导致位移累积错乱。
    /// </summary>
    private Dictionary<string, double> CaptureCardOffsets(bool withTransform)
    {
        var map = new Dictionary<string, double>();
        foreach (var kv in _cards)
        {
            var root = kv.Value.Root;
            if (root == null) continue;
            var y = root.TranslatePoint(new System.Windows.Point(0, 0), StockPanel).Y;
            if (!withTransform && root.RenderTransform is TranslateTransform tt) y -= tt.Y;
            map[kv.Key] = y;
        }
        return map;
    }

    /// <summary>
    /// 让正在拖拽的卡片实时跟随鼠标（直接设 TranslateTransform，不加动画）。
    /// 这是"手感顺不顺"的关键：此前被拖拽项只能"跳到"布局位置，与其余卡片的平滑让位形成对比，显得生硬；
    /// 改成跟随后，拖起来像真的拎着这张卡片走。
    /// </summary>
    private void FollowPointer()
    {
        if (_dragCode == null) return;
        if (!_cards.TryGetValue(_dragCode, out var card) || card.Root == null) return;
        var root = card.Root;

        // 布局位置 = 当前视觉位置扣掉临时偏移（TranslatePoint 含 RenderTransform 影响）
        var layoutY = root.TranslatePoint(new System.Windows.Point(0, 0), StockPanel).Y;
        if (root.RenderTransform is TranslateTransform old) layoutY -= old.Y;

        var offset = (_dragPointerY - _dragGrabOffset) - layoutY;   // 保持"抓在原处"的手感
        var tt = root.RenderTransform as TranslateTransform;
        if (tt == null) { tt = new TranslateTransform(); root.RenderTransform = tt; }
        tt.BeginAnimation(TranslateTransform.YProperty, null);       // 跟手移动不做补间
        tt.Y = offset;
    }

    /// <summary>
    /// 给正在拖拽的卡片加「背景蒙版」：底色由 6% 提到 22% 实色，并加一层柔和阴影。
    /// 卡片原本是半透明的，跟随鼠标移动时会与下方卡片重叠、互相透视导致文字穿插看不清；
    /// 变实 + 阴影后既有明确层次，也有"被拎起来"的观感。
    /// </summary>
    private void ApplyDragMask(Border root)
    {
        _dragOrigBackground = root.Background;
        _dragOrigEffect = root.Effect;

        var cardColor = (ThemeBrush("CardBgBrush") as SolidColorBrush)?.Color ?? Colors.White;
        root.Background = new SolidColorBrush(cardColor) { Opacity = 0.22 };
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.38,
            Color = Colors.Black
        };
    }

    /// <summary>移除拖拽蒙版，恢复卡片原本的底色与效果</summary>
    private void ClearDragMask(Border root)
    {
        if (_dragOrigBackground != null) root.Background = _dragOrigBackground;
        root.Effect = _dragOrigEffect;
        _dragOrigBackground = null;
        _dragOrigEffect = null;
    }

    /// <summary>
    /// 换位过渡动画（FLIP）：让每张卡片的视觉位置从「换位前」平滑滑到「换位后」。
    /// 实现上先把它瞬时拉回旧视觉位置（invert），再用 150ms EaseOut 动画归零。
    /// 关键：对已有的 TranslateTransform **先定格当前动画值再改**，
    /// 这样连续拖拽时新动画从上一次的中间态接着走，不会抽搐或跳位。
    /// 正在被拖拽的那张（draggingCode）不做补间，保持跟手。
    /// </summary>
    private void PlayReorderAnimation(Dictionary<string, double> visualBefore, Dictionary<string, double> layoutAfter, string? draggingCode)
    {
        foreach (var kv in layoutAfter)
        {
            if (!_cards.TryGetValue(kv.Key, out var card) || card.Root == null) continue;
            var root = card.Root;

            if (kv.Key == draggingCode) continue;   // 被拖拽项已由 FollowPointer 定位，不参与补间
            if (!visualBefore.TryGetValue(kv.Key, out var v0)) continue;

            var invert = v0 - kv.Value;      // 先让视觉回到换位前，所需的临时偏移
            var tt = root.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                root.RenderTransform = tt;
            }
            else
            {
                var current = tt.Y;                                   // 定格动画中间值
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                tt.Y = current;
            }

            tt.Y = invert;
            // 300ms + 正弦缓出：SineEase 的初速比二次/三次缓出都低，起步轻、收尾最柔，
            // 「其余卡片让位」像被缓缓推开，而不是弹过去。
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>
    /// 结束拖拽排序：释放捕获、恢复悬停探测、隐藏手柄；
    /// 被拖拽的卡片从"跟手偏移"**平滑归位**到布局位置（200ms，而不是瞬间跳回），
    /// 最后通知外部持久化 + 同步主窗口列表。
    /// </summary>
    private void EndDragSort(FrameworkElement handle)
    {
        if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
        handle.Opacity = 0;
        _dragging = false;
        if (_dragCode == null) return;

        if (_cards.TryGetValue(_dragCode, out var card) && card.Root != null)
        {
            System.Windows.Controls.Panel.SetZIndex(card.Root, 0);
            ClearDragMask(card.Root);   // 移除背景蒙版，恢复原本的底色与效果
            // 松手时卡片通常停在"两格之间"，用一小段动画滑回槽位，避免硬切
            if (card.Root.RenderTransform is TranslateTransform tt && Math.Abs(tt.Y) > 0.5)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }

        _dragCode = null;
        _lastDropTarget = null;
        OrderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按内容自动测量并设置窗口尺寸（数据超出默认显示范围时放大窗口，限制不超过工作区）</summary>
    private void AutoFit()
    {
        RootBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = RootBorder.DesiredSize;
        var area = SystemParameters.WorkArea;
        // 极简模式贴合内容即可（紧凑）；常规模式保留较宽松的下限，避免窗口过小不好点选
        var minW = _settings.MinimalMode ? 60 : 220;
        var minH = _settings.MinimalMode ? 24 : 90;
        var w = Math.Min(Math.Max(sz.Width + 4, minW), area.Width * 0.9);
        var h = Math.Min(Math.Max(sz.Height + 4, minH), area.Height * 0.92);
        Width = w;
        Height = h;

        // 首次（尺寸尚未定稿前）跟随贴到右下角，带固定边距
        if (_needPlace) PlaceBottomRight();
    }

    /// <summary>把盯盘窗口贴到屏幕工作区右下角（预留边距）</summary>
    private void PlaceBottomRight()
    {
        const double gap = 24;
        var area = SystemParameters.WorkArea;
        var w = Width > 1 ? Width : 440;
        var h = Height > 1 ? Height : 260;
        Left = Math.Max(area.Left + gap, area.Right - w - gap);
        Top = Math.Max(area.Top + gap, area.Bottom - h - gap);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        // 隐藏（托盘收起 / 悬停自动隐藏 / 用户关闭显示）时不刷新：没人看就不必打接口，省带宽与 CPU。
        // 重新显示时由 IsVisibleChanged 立刻补一次。
        if (!IsVisible) return;
        var codes = VisibleStocks();
        if (codes.Count == 0) return;
        _refreshing = true;
        try
        {
            var quotes = await TencentStockClient.GetQuotesAsync(codes);
            Dispatcher.Invoke(() => UpdateUI(quotes));
        }
        catch
        {
            // 网络异常忽略，下一周期重试
        }
        finally { _refreshing = false; }
    }

    private void UpdateUI(List<StockQuote> quotes)
    {
        foreach (var q in quotes)
        {
            if (!_cards.TryGetValue(q.Code, out var card)) continue;
            var def = new SolidColorBrush(ColorFromHex(_settings.ForegroundColor));

            if (card.NameTb != null) card.NameTb.Text = q.Name;
            if (card.PriceTb != null)
            {
                card.PriceTb.Text = q.Price?.ToString("F2") ?? "-";
                card.PriceTb.Foreground = BrushFor(FieldKeys.Price, q, def);
            }
            if (card.ChangeTb != null)
            {
                card.ChangeTb.Text = FieldFormatter.GetText(q, FieldKeys.ChangePercent);
                card.ChangeTb.Foreground = BrushFor(FieldKeys.ChangePercent, q, def);
            }

            // 快速涨跌箭头（单次刷新价格变动占比 >= 阈值）
            if (card.ArrowTb != null)
            {
                if (_prevPrices.TryGetValue(q.Code, out var prev) && q.Price.HasValue && prev.HasValue)
                {
                    var delta = q.Price.Value - prev.Value;
                    var ratio = prev.Value != 0 ? Math.Abs(delta) / prev.Value : 0;
                    if ((double)ratio >= (double)_settings.FastMoveThreshold)
                    {
                        card.ArrowTb.Text = delta > 0 ? "▲" : "▼";
                        card.ArrowTb.Foreground = delta > 0 ? RedBrush() : GreenBrush();
                    }
                    else
                    {
                        card.ArrowTb.Text = "";
                    }
                }
                else
                {
                    card.ArrowTb.Text = "";
                }
                _prevPrices[q.Code] = q.Price;
            }

            foreach (var f in _settings.Fields)
            {
                if (!f.IsEnabled) continue;
                if (f.Key is FieldKeys.Name or FieldKeys.Price or FieldKeys.ChangePercent) continue;
                if (card.Chips.TryGetValue(f.Key, out var tb))
                {
                    tb.Text = FieldFormatter.GetText(q, f.Key);
                    tb.Foreground = BrushFor(f.Key, q, def);
                }
            }
        }

        // 首次数据到达后精调一次窗口尺寸（此后位置交给用户）
        if (_needFit)
        {
            AutoFit();
            _needFit = false;
            _needPlace = false;
        }
    }

    private System.Windows.Media.Brush BrushFor(string key, StockQuote q, System.Windows.Media.Brush def)
    {
        var f = _settings.Fields.FirstOrDefault(x => x.Key == key);
        if (f == null) return def;
        if (f.ColorMode == ColorMode.Fixed) return new SolidColorBrush(ColorFromHex(f.Color));
        if (q.ChangePercent > 0) return RedBrush();
        if (q.ChangePercent < 0) return GreenBrush();
        return def;
    }

    private static SolidColorBrush RedBrush() => new(System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x38));
    private static SolidColorBrush GreenBrush() => new(System.Windows.Media.Color.FromRgb(0x1D, 0xBF, 0x60));

    private static System.Windows.Media.Color ColorFromHex(string hex)
    {
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!; }
        catch { return Colors.White; }
    }

    /// <summary>从应用级资源字典取主题 Brush（明暗切换后自动更新）</summary>
    private static System.Windows.Media.Brush? ThemeBrush(string key)
    {
        try { return System.Windows.Application.Current.Resources[key] as System.Windows.Media.Brush; }
        catch { return null; }
    }

    /// <summary>悬停模式：光标进入窗口区域则显示，移出且不在窗口上则隐藏</summary>
    private void HoverWatchTick()
    {
        if (_dragging) return;
        if (!_settings.HoverToShow) return;
        var inside = IsCursorInsideWindow();
        if (inside && !IsVisible) Show();
        else if (!inside && IsVisible && !IsMouseOver) Hide();
    }

    private bool IsCursorInsideWindow()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        // Cursor.Position 与 GetWindowRect 同为物理像素，避免高 DPI 下坐标系不一致
        var p = System.Windows.Forms.Cursor.Position;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    /// <summary>
    /// 左键按下任意位置即开始拖动窗口；按在卡片拖拽手柄上时不拖窗口（交给手柄做排序）。
    ///
    /// **这里不用 DragMove()，改为手动改 Left/Top**：
    /// `DragMove()` 依赖窗口的非客户区消息，在极简模式下（窗口极小、且背景完全透明）会失效，
    /// 表现为"按住左键窗口不动"。手动实现只依赖屏幕光标坐标，任何模式都可靠。
    /// 定位用「起点绝对坐标 + 总位移」，不用逐帧累加，避免漂移。
    /// </summary>
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsUnderDragHandle(e.OriginalSource as DependencyObject)) return;

        var p = System.Windows.Forms.Cursor.Position;
        var (sx, sy) = DeviceToLogicalScale();   // 物理像素 → 逻辑单位，否则高 DPI 下位移被缩放、不跟手
        _dragStartScreenX = p.X / sx;
        _dragStartScreenY = p.Y / sy;
        _dragOriginLeft = Left;
        _dragOriginTop = Top;
        _windowDragging = true;
        _dragging = true;                 // 暂停「鼠标移出自动隐藏」，避免拖动时窗口被藏掉
        CaptureMouse();                   // 保证拖到窗口外仍能收到 Move / Up
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_windowDragging) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { EndWindowDrag(); return; }
        var p = System.Windows.Forms.Cursor.Position;
        var (sx, sy) = DeviceToLogicalScale();
        Left = _dragOriginLeft + (p.X / sx - _dragStartScreenX);
        Top = _dragOriginTop + (p.Y / sy - _dragStartScreenY);
    }

    /// <summary>
    /// 物理像素 → WPF 逻辑单位（DIP）的换算系数。
    /// `System.Windows.Forms.Cursor.Position` 是物理像素，而窗口的 Left/Top 是逻辑单位；
    /// 高 DPI（125% / 150% …）下若不换算，拖动位移会被缩放，表现为"拖了但跟手不对"。
    /// </summary>
    private (double x, double y) DeviceToLogicalScale()
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        if (m == null) return (1.0, 1.0);
        return (m.Value.M11 <= 0 ? 1.0 : m.Value.M11, m.Value.M22 <= 0 ? 1.0 : m.Value.M22);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_windowDragging) return;
        EndWindowDrag();
        e.Handled = true;
    }

    private void EndWindowDrag()
    {
        if (!_windowDragging) return;
        _windowDragging = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>从命中元素向上找带 DragHandle 标记的祖先（判断是否按在拖拽手柄上）</summary>
    private static bool IsUnderDragHandle(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && Equals(fe.Tag, "DragHandle")) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) Hide();
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsVisible) Show();
        SetDragLayerHover(true);
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 拖动窗口或拖拽排序期间不隐藏：拖快时鼠标会短暂移出窗口边界，
        // 此时若触发隐藏，窗口会在拖动中途消失（悬停显示模式下尤为明显）
        if (_dragging) return;
        SetDragLayerHover(false);
        if (_settings.HoverToShow) Hide();
    }

    /// <summary>
    /// 极简模式下遮罩的悬停反馈：鼠标移入 → 10% 暗灰；移出 → 完全透明。
    /// 极简窗口本身完全透明，悬停时给一层极淡的底衬，既提示"这里可以按住了拖动"，也让文字更易看清。
    /// 常规模式不受影响（遮罩此时是隐藏的）。
    /// </summary>
    private void SetDragLayerHover(bool inside)
    {
        if (!_settings.MinimalMode) return;
        // 0x1A ≈ 10% 不透明度；#202020 为暗灰
        DragLayer.Background = inside
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x20, 0x20, 0x20))
            : System.Windows.Media.Brushes.Transparent;
    }
}
