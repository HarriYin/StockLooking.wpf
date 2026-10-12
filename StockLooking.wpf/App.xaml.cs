using System;
using System.Windows;
using StockLooking.wpf.Models;
using StockLooking.wpf.Services;
using StockLooking.wpf.Views;

namespace StockLooking.wpf;

public partial class App : System.Windows.Application
{
    private AppSettings _settings = new();
    private MonitorWindow? _monitor;
    private MainWindow? _main;
    private TrayIconService? _tray;
    /// <summary>闲时定期刷新「股票全量名录」，保证长年不重启也能收录新上市的票</summary>
    private System.Windows.Threading.DispatcherTimer? _catalogTimer;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = SettingsService.Load();
        ThemeManager.Apply(_settings.Theme);

        // 预热「股票全量名录」：异步拉取/读缓存，供所有搜索框做任意位置模糊匹配。
        // 放后台且不 await —— 首次打开即可用，不阻塞启动；失败也由 StockCatalog 内部兜底（退化为 smartbox）。
        _ = StockCatalog.EnsureLoadedAsync();

        // 运行期定期刷新名录（每 1 小时检查一次，数据超过 RefreshAge=6h 就后台重拉）：
        // 托盘程序常年不重启，只靠"启动加载"会让日后新上市的票搜不到 —— 这个定时器负责补上。
        _catalogTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1)
        };
        _catalogTimer.Tick += (_, _) => _ = StockCatalog.RefreshIfStaleAsync(StockCatalog.RefreshAge);
        _catalogTimer.Start();

        _monitor = new MonitorWindow(_settings);
        // 盯盘窗口内拖拽排序后：立即持久化，并让已打开的主窗口同步刷新列表（两处顺序必须一致）
        _monitor.OrderChanged += (_, _) =>
        {
            SettingsService.Save(_settings);
            _main?.SyncWatchOrder();
        };
        // 窗口自身可见性变化（双击隐藏 / 悬停自动隐藏）时同步托盘菜单勾选态
        _monitor.VisibilityChanged += (_, visible) => _tray?.SyncMonitorVisible(visible);

        _tray = new TrayIconService();
        _tray.MonitorVisibilityChanged += (_, visible) => SetMonitorVisible(visible);
        _tray.MinimalModeChanged += (_, on) => ApplyMinimalModeFromTray(on);
        _tray.OpenMain += (_, _) => ShowMain();
        _tray.Exit += (_, _) => ShutdownApp();

        // 启动默认显示盯盘窗口，主窗口不显示
        SetMonitorVisible(true);

        // 托盘菜单的**初始**勾选态必须与已保存的设置一致：
        // 否则上次退出时开着的极简模式，重启后菜单仍显示为未勾选（菜单默认 false，设置里是 true）。
        _tray.SyncState(_monitor?.IsVisible ?? false, _settings.MinimalMode);
    }

    /// <summary>显示 / 隐藏盯盘窗口（托盘菜单勾选项与左键单击都走这里），并回写菜单勾选态</summary>
    private void SetMonitorVisible(bool visible)
    {
        if (_monitor == null) return;
        if (visible)
        {
            if (!_monitor.IsVisible) _monitor.Show();
            _monitor.Activate();
        }
        else _monitor.Hide();
        _tray?.SyncMonitorVisible(_monitor.IsVisible);
    }

    /// <summary>托盘菜单切换极简模式：写设置 → 持久化 → 应用到盯盘窗口 → 回填设置页勾选框</summary>
    private void ApplyMinimalModeFromTray(bool on)
    {
        _settings.MinimalMode = on;
        SettingsService.Save(_settings);
        _monitor?.ApplySettings(_settings);
        _main?.SyncMinimalMode(on);
    }

    /// <summary>首次打开时按需创建主窗口，避免启动即加载设置界面</summary>
    private void ShowMain()
    {
        if (_main == null)
        {
            _main = new MainWindow(_settings);
            _main.SettingsChanged += (_, ns) =>
            {
                _monitor?.ApplySettings(ns);
                // 设置页里改动了极简模式等状态 → 同步托盘菜单的勾选态
                _tray?.SyncState(_monitor?.IsVisible ?? false, ns.MinimalMode);
            };
        }
        _main.RefreshDetailStocks();
        if (!_main.IsVisible) _main.Show();
        _main.Activate();

        // 打开主窗口时再对齐一次：托盘菜单与设置页的勾选态始终一致
        _tray?.SyncState(_monitor?.IsVisible ?? false, _settings.MinimalMode);
    }

    private void ShutdownApp()
    {
        _catalogTimer?.Stop();
        _catalogTimer = null;
        _tray?.Dispose();
        _tray = null;
        _monitor?.Close();
        _main?.ForceClose();
        Shutdown();
    }
}
