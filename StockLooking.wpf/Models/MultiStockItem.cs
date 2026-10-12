using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockLooking.wpf.Services;

namespace StockLooking.wpf.Models;

/// <summary>
/// 「多股K线」页的容器项：一只票在容器中的一份记录（代码/名称/配色/是否显示/数据）。
/// 与分组自选的共享行不同 —— 这里每只票只属于本容器一份，删除只影响本页 K 线。
/// 实现 INotifyPropertyChanged：名称异步补全后自动刷新界面。
/// </summary>
public class MultiStockItem : INotifyPropertyChanged
{
    private string _name = "";
    private bool _isVisible = true;
    private string _status = "";

    public string Code { get; set; } = "";

    /// <summary>股票名称（可能异步补全；为空时显示代码）</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>本票在 K 线图中的固定颜色（不再按涨跌变红变绿）</summary>
    public string ColorHex { get; set; } = "#FF9F0A";

    /// <summary>是否在 K 线图中显示（容器左侧勾选框）</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    /// <summary>加载状态/错误提示（界面上不展示，靠状态栏汇总）</summary>
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }

    // ===== 数据（非界面绑定，直接由控件持有）=====
    /// <summary>K线/分时数据；加载失败保持 null</summary>
    public System.Collections.Generic.List<KLineItem>? Klines;
    /// <summary>MACD 结果，与 Klines 下标一一对应</summary>
    public MacdResult? Macd;
    /// <summary>分时昨收（仅分时图有意义，用于涨跌幅基准）</summary>
    public decimal? PrevClose;
    /// <summary>最近一次成功加载时的周期，用于判断数据是否"过期"（切周期后需重取）</summary>
    public string LoadedType = "";

    public string Display => string.IsNullOrWhiteSpace(_name) ? Code : _name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
