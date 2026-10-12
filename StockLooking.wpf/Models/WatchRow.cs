using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StockLooking.wpf.Models;

/// <summary>
/// 自选股显示行：勾选状态 + 代码 + 名称 + 板块信息 + 实时行情。
/// 实现 INotifyPropertyChanged，使异步拉取到的名称/行情能直接刷新界面（列表与明细下拉共用）。
/// </summary>
public class WatchRow : INotifyPropertyChanged
{
    private string _name = "";
    private decimal? _price;
    private decimal? _changePercent;
    private decimal? _changeAmount;
    private decimal? _amplitude;
    private decimal? _open;
    private decimal? _high;
    private decimal? _low;
    private decimal? _prevClose;
    private decimal? _pe;
    private long _volume;
    private decimal? _amount;
    private decimal? _turnover;

    public string Code { get; set; } = "";

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
    }

    public string SectorName { get; set; } = "";
    public string SectorColor { get; set; } = "#8E8E93";
    public int SectorOrder { get; set; }

    /// <summary>是否在盯盘界面显示（true 显示；首次添加默认 true）</summary>
    public bool IsSelected { get; set; } = true;

    // ===== 实时行情（异步拉取后填充） =====
    /// <summary>当前价</summary>
    public decimal? Price
    {
        get => _price;
        set { if (_price == value) return; _price = value; OnPropertyChanged(); OnPropertyChanged(nameof(PriceText)); }
    }

    /// <summary>涨跌百分比</summary>
    public decimal? ChangePercent
    {
        get => _changePercent;
        set
        {
            if (_changePercent == value) return;
            _changePercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChangeText));
            OnPropertyChanged(nameof(ChangeColorHex));
        }
    }

    /// <summary>涨跌额（绝对价差）</summary>
    public decimal? ChangeAmount
    {
        get => _changeAmount;
        set
        {
            if (_changeAmount == value) return;
            _changeAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChangeAmountText));
        }
    }

    /// <summary>振幅%（(最高-最低)/昨收×100）</summary>
    public decimal? Amplitude
    {
        get => _amplitude;
        set
        {
            if (_amplitude == value) return;
            _amplitude = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AmplitudeText));
        }
    }

    /// <summary>今开</summary>
    public decimal? Open
    {
        get => _open;
        set { if (_open == value) return; _open = value; OnPropertyChanged(); OnPropertyChanged(nameof(OpenText)); }
    }

    /// <summary>最高</summary>
    public decimal? High
    {
        get => _high;
        set { if (_high == value) return; _high = value; OnPropertyChanged(); OnPropertyChanged(nameof(HighText)); }
    }

    /// <summary>最低</summary>
    public decimal? Low
    {
        get => _low;
        set { if (_low == value) return; _low = value; OnPropertyChanged(); OnPropertyChanged(nameof(LowText)); }
    }

    /// <summary>昨收</summary>
    public decimal? PrevClose
    {
        get => _prevClose;
        set { if (_prevClose == value) return; _prevClose = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrevCloseText)); }
    }

    /// <summary>市盈率（动态）</summary>
    public decimal? PE
    {
        get => _pe;
        set { if (_pe == value) return; _pe = value; OnPropertyChanged(); OnPropertyChanged(nameof(PEText)); }
    }

    /// <summary>成交量（手）</summary>
    public long Volume
    {
        get => _volume;
        set { if (_volume == value) return; _volume = value; OnPropertyChanged(); OnPropertyChanged(nameof(VolumeText)); }
    }

    /// <summary>成交额（万元）</summary>
    public decimal? Amount
    {
        get => _amount;
        set { if (_amount == value) return; _amount = value; OnPropertyChanged(); OnPropertyChanged(nameof(AmountText)); }
    }

    /// <summary>换手率（%）</summary>
    public decimal? Turnover
    {
        get => _turnover;
        set { if (_turnover == value) return; _turnover = value; OnPropertyChanged(); OnPropertyChanged(nameof(TurnoverText)); }
    }

    /// <summary>
    /// 把一次行情快照写回本行。
    /// 名称仅在接口返回非空时覆盖，避免偶发缺名把已有名称清成空白；
    /// 涨跌额、振幅这两项腾讯接口有时缺失，缺失时按「现价-昨收」「(高-低)/昨收」自行推算。
    /// </summary>
    public void ApplyQuote(StockQuote q)
    {
        if (q == null) return;
        if (!string.IsNullOrWhiteSpace(q.Name)) Name = q.Name;
        Price = q.Price;
        ChangePercent = q.ChangePercent;
        ChangeAmount = q.Change ?? (q.Price.HasValue && q.PrevClose.HasValue ? q.Price.Value - q.PrevClose.Value : null);
        Amplitude = q.High.HasValue && q.Low.HasValue && q.PrevClose is > 0
            ? (q.High.Value - q.Low.Value) / q.PrevClose.Value * 100m
            : null;
        Open = q.Open;
        High = q.High;
        Low = q.Low;
        PrevClose = q.PrevClose;
        PE = q.PE;
        Volume = q.Volume;
        Amount = q.Amount;
        Turnover = q.Turnover;
    }

    /// <summary>优先显示名称，缺失时回退代码</summary>
    public string Display => string.IsNullOrWhiteSpace(_name) ? Code : _name;

    public string PriceText => Price?.ToString("F2") ?? "-";
    public string ChangeText => ChangePercent.HasValue
        ? (ChangePercent.Value > 0 ? "+" : "") + ChangePercent.Value.ToString("F2") + "%"
        : "-";
    public string ChangeAmountText => ChangeAmount.HasValue
        ? (ChangeAmount.Value > 0 ? "+" : "") + ChangeAmount.Value.ToString("F2")
        : "-";
    public string AmplitudeText => Amplitude.HasValue ? Amplitude.Value.ToString("F2") + "%" : "-";
    public string OpenText => Open?.ToString("F2") ?? "-";
    public string HighText => High?.ToString("F2") ?? "-";
    public string LowText => Low?.ToString("F2") ?? "-";
    public string PrevCloseText => PrevClose?.ToString("F2") ?? "-";
    public string PEText => PE is > 0 ? PE.Value.ToString("F1") : "-";   // 亏损/缺失时不显示负市盈率
    public string VolumeText => Volume > 0 ? Volume.ToString("N0") : "-";
    public string AmountText => Amount.HasValue ? Amount.Value.ToString("N0") : "-";
    public string TurnoverText => Turnover.HasValue ? Turnover.Value.ToString("F2") + "%" : "-";

    /// <summary>涨红跌绿配色（A股惯例），平盘用灰</summary>
    public string ChangeColorHex => ChangePercent > 0 ? "#FF4438"
        : ChangePercent < 0 ? "#1DBF60" : "#8E8E93";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
