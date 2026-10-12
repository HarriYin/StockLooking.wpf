using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using StockLooking.wpf;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace StockLooking.wpf.Services;

/// <summary>
/// 系统托盘图标封装（图标本体仍用 WinForms NotifyIcon，右键菜单改用 WPF 自绘的 Apple 风样式）。
/// 菜单里的「显示盯盘窗口」「极简模式」都是**勾选项**：勾选 = 开，取消勾选 = 关。
/// </summary>
/// <remarks>
/// 为什么不再用 WinForms 的 ContextMenuStrip：那是 Win32 原生菜单，
/// 圆角、内边距、强调色高亮、半透明材质都做不了，与全 App 的 Apple 风格不一致。
/// 这里改为自行弹出 WPF 菜单，样式统一放在 App.xaml（键：AppleContextMenu / AppleMenuItem / AppleSeparator）。
/// 托盘场景在 <see cref="Menu_Opened"/> 里必须把菜单窗口提到前台，否则点击菜单外区域时它不会自动关闭。
/// </remarks>
public class TrayIconService : IDisposable
{
    private const string IconPackUri = "pack://application:,,,/Resources/stock-up.ico";
    private const string ContextMenuStyleKey = "AppleContextMenu";
    private const string MenuItemStyleKey = "AppleMenuItem";
    private const string SeparatorStyleKey = "AppleSeparator";

    private readonly NotifyIcon _notify;
    private readonly WpfMenuItem _monitorItem;
    private readonly WpfMenuItem _minimalItem;
    private readonly WpfContextMenu _menu;

    public event EventHandler? OpenMain;
    public event EventHandler? Exit;
    /// <summary>用户切换「显示盯盘窗口」：true = 显示，false = 隐藏</summary>
    public event EventHandler<bool>? MonitorVisibilityChanged;
    /// <summary>用户切换「极简模式」</summary>
    public event EventHandler<bool>? MinimalModeChanged;

    public TrayIconService()
    {
        _notify = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "StockLooking 盯盘",
            Visible = true
        };

        // 显示/隐藏合并为一项：勾选 = 显示盯盘窗口，取消勾选 = 隐藏
        _monitorItem = NewCheckItem("显示盯盘窗口", MonitorItem_Click);
        _minimalItem = NewCheckItem("极简模式", MinimalItem_Click);
        var openItem = NewItem("打开主窗口", (_, _) => OpenMain?.Invoke(this, EventArgs.Empty));
        var exitItem = NewItem("退出", (_, _) => Exit?.Invoke(this, EventArgs.Empty));

        _menu = new WpfContextMenu
        {
            // 鼠标位置起点：右键点击发生在光标处，交给 WPF 换算物理像素 → DIP，无需自己做 DPI 换算
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            StaysOpen = false
        };
        if (TryFindStyle(ContextMenuStyleKey) is System.Windows.Style menuStyle) _menu.Style = menuStyle;

        _menu.Items.Add(_monitorItem);
        _menu.Items.Add(_minimalItem);
        _menu.Items.Add(openItem);
        _menu.Items.Add(NewSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opened += Menu_Opened;

        _notify.MouseClick += Notify_MouseClick;
    }

    private void Notify_MouseClick(object? sender, MouseEventArgs e)
    {
        // 右键：弹出 Apple 风菜单（不再托管给 WinForms 的原生菜单）
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        // 左键单击托盘图标：切换盯盘窗口显示（与菜单勾选保持同一套语义）
        _monitorItem.IsChecked = !_monitorItem.IsChecked;
        MonitorVisibilityChanged?.Invoke(this, _monitorItem.IsChecked);
    }

    /// <summary>右键弹出菜单；已打开时先关掉，避免重复右键时菜单闪烁或不跟随光标</summary>
    private void ShowMenu()
    {
        if (_menu.IsOpen)
        {
            _menu.IsOpen = false;
            return;
        }
        _menu.IsOpen = true;
    }

    private void MonitorItem_Click(object sender, RoutedEventArgs e)
        => MonitorVisibilityChanged?.Invoke(this, _monitorItem.IsChecked);

    private void MinimalItem_Click(object sender, RoutedEventArgs e)
        => MinimalModeChanged?.Invoke(this, _minimalItem.IsChecked);

    /// <summary>
    /// 托盘场景的关键一步：菜单弹出后把它所在的窗口设为前台窗口。
    /// WPF 依赖"菜单窗口失去激活"来触发自动关闭；托盘里打开时前台通常是资源管理器或其它应用窗口，
    /// 不抢焦点的话，点击菜单以外的区域菜单会一直挂着不消失。
    /// </summary>
    private static void Menu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfContextMenu menu) return;
        if (PresentationSource.FromVisual(menu) is HwndSource src)
            SetForegroundWindow(src.Handle);
    }

    /// <summary>
    /// 由外部同步两个勾选态。设置页改极简模式、盯盘窗口被双击隐藏/悬停自动隐藏等都会改变实际状态，
    /// 不回调这里菜单就会与实际不一致。仅赋值 IsChecked 不会触发 Click，故不会回环。
    /// </summary>
    public void SyncState(bool monitorVisible, bool minimalMode)
    {
        _monitorItem.IsChecked = monitorVisible;
        _minimalItem.IsChecked = minimalMode;
    }

    /// <summary>仅同步「显示盯盘窗口」的勾选态（窗口自身可见性变化时调用）</summary>
    public void SyncMonitorVisible(bool visible) => _monitorItem.IsChecked = visible;

    // ===== 菜单构造：样式缺失时仍然要保证菜单可用（退化但功能不丢），所以样式一律 Try 系列 =====
    private static WpfMenuItem NewCheckItem(string header, RoutedEventHandler onClick)
    {
        var item = NewItem(header, onClick);
        // 可勾选：WPF 会在 Click **之前**翻转 IsChecked，事件里直接读到的就是新状态
        item.IsCheckable = true;
        return item;
    }

    private static WpfMenuItem NewItem(string header, RoutedEventHandler onClick)
    {
        var item = new WpfMenuItem { Header = header };
        if (TryFindStyle(MenuItemStyleKey) is System.Windows.Style style) item.Style = style;
        item.Click += onClick;
        return item;
    }

    private static WpfSeparator NewSeparator()
    {
        var sep = new WpfSeparator();
        if (TryFindStyle(SeparatorStyleKey) is System.Windows.Style style) sep.Style = style;
        return sep;
    }

    private static System.Windows.Style? TryFindStyle(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Style;

    /// <summary>从嵌入资源加载应用图标，失败时回退系统默认图标</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri(IconPackUri, UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream != null)
            {
                using var src = info.Stream;
                using var ms = new MemoryStream();
                src.CopyTo(ms);
                ms.Position = 0;
                // 托盘按系统小图标尺寸渲染，取 16x16 帧最清晰
                return new Icon(ms, SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // 资源缺失时回退
        }
        return SystemIcons.Application;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void Dispose()
    {
        _menu.Opened -= Menu_Opened;
        _menu.IsOpen = false;
        _notify.MouseClick -= Notify_MouseClick;
        _notify.Visible = false;
        _notify.Dispose();
    }
}
