using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace StockLooking.wpf.Models;

/// <summary>
/// 分组视图模型：包装 <see cref="StockGroup"/>（持久化模型），并维护可绑定的行情行集合。
/// Rows 中的 <see cref="WatchRow"/> 实例在各分组间共享（同一代码只持有一份），
/// 行情刷新时就地更新属性即可驱动所有分组卡片同步刷新。
/// </summary>
public class GroupViewModel : INotifyPropertyChanged
{
    private readonly StockGroup _model;

    public StockGroup Model => _model;

    public string Name
    {
        get => _model.Name;
        set
        {
            if (_model.Name == value) return;
            _model.Name = value;
            OnChanged();
        }
    }

    /// <summary>分组内股票行情行（共享 WatchRow 实例）</summary>
    public ObservableCollection<WatchRow> Rows { get; } = new();

    /// <summary>
    /// 分组内股票计数（如「(12/100)」），显示在**卡片右下角**。
    /// 名称不在此处重复 —— 它就在卡片左上角的可编辑框里。
    /// </summary>
    public string CountText => $"({Rows.Count}/{StockGroup.MaxItems})";

    // 三种排序模式的图标（16×16 视口）：
    //   加入顺序 = 三条等长横线；涨幅优先 = 递减条 + 下箭头；跌幅优先 = 递增条 + 上箭头。
    private const string IconSortNatural = "M3,4.5 H13 M3,8 H13 M3,11.5 H13";
    private const string IconSortDesc = "M2,4.5 H8 M2,8 H6 M2,11.5 H4 M12.5,3.5 V11 M10.5,9 L12.5,11 L14.5,9";
    private const string IconSortAsc = "M2,4.5 H4 M2,8 H6 M2,11.5 H8 M12.5,11 V3.5 M10.5,5.5 L12.5,3.5 L14.5,5.5";

    /// <summary>组内排序模式：0 = 加入顺序（Codes 顺序），1 = 涨幅优先（降序），2 = 跌幅优先（升序）</summary>
    public int SortMode { get; private set; }

    /// <summary>当前排序模式对应的图标几何（供卡片头按钮直接绑定，避免 DataTrigger 里做 int 比较）</summary>
    public string SortIcon { get; private set; } = IconSortNatural;

    /// <summary>鼠标悬停提示（保持简短，图标本身已表达方向）</summary>
    public string SortHint { get; private set; } = "加入顺序";

    /// <summary>循环切换排序并立即应用：涨幅优先 → 跌幅优先 → 加入顺序</summary>
    public int ToggleSort()
    {
        SortMode = (SortMode + 1) % 3;
        (SortIcon, SortHint) = SortMode switch
        {
            1 => (IconSortDesc, "涨幅优先"),
            2 => (IconSortAsc, "跌幅优先"),
            _ => (IconSortNatural, "加入顺序")
        };
        OnChanged(nameof(SortMode));
        OnChanged(nameof(SortIcon));
        OnChanged(nameof(SortHint));
        ApplySort();
        return SortMode;
    }

    /// <summary>
    /// 按当前排序模式重排 <see cref="Rows"/>。只影响显示，**不改动持久化的 <c>Codes</c> 顺序**，
    /// 因此行情每次刷新后重新调用即可始终按最新涨跌幅排列。
    /// </summary>
    public void ApplySort()
    {
        if (Rows.Count <= 1) return;

        // 模式 0 = 加入顺序：显示顺序必须**还原**为持久化的 Codes 顺序。
        // 此前直接 return —— 从涨幅/跌幅切回来时 Rows 还停在排序结果上，
        // 界面看起来就像"默认排序 = 正序结果"。这里按 Codes 的次序把 Rows 摆回去。
        if (SortMode == 0)
        {
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _model.Codes.Count; i++)
                if (!rank.ContainsKey(_model.Codes[i])) rank[_model.Codes[i]] = i;

            var natural = Rows.OrderBy(r => rank.TryGetValue(r.Code, out var k) ? k : int.MaxValue).ToList();
            for (var i = 0; i < natural.Count; i++)
            {
                var cur = Rows.IndexOf(natural[i]);
                if (cur != i) Rows.Move(cur, i);
            }
            return;
        }

        var sorted = (SortMode == 1
            ? Rows.OrderByDescending(r => r.ChangePercent)
            : Rows.OrderBy(r => r.ChangePercent)).ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var cur = Rows.IndexOf(sorted[i]);
            if (cur != i) Rows.Move(cur, i);
        }
    }

    /// <summary>
    /// 手动拖拽排序结束后调用：把当前**显示顺序**写回持久化的 <c>Codes</c>。
    /// ★ 同时把排序模式切回「加入顺序」—— 手动拖拽本身就是"自定义顺序"的明确表达，
    ///   此时再挂着涨幅/跌幅模式会被下一次行情刷新重新打乱，等于拖拽白做。
    /// </summary>
    public void ApplyManualOrder()
    {
        _model.Codes = Rows.Select(r => r.Code).ToList();

        if (SortMode == 0) return;
        SortMode = 0;
        (SortIcon, SortHint) = (IconSortNatural, "加入顺序");
        OnChanged(nameof(SortMode));
        OnChanged(nameof(SortIcon));
        OnChanged(nameof(SortHint));
    }

    public GroupViewModel(StockGroup model)
    {
        _model = model;
        // 成员变化时刷新右下角计数
        Rows.CollectionChanged += (_, _) => OnChanged(nameof(CountText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
