namespace StockLooking.wpf.Models;

/// <summary>
/// 「添加到哪些分组」弹窗中的一行：勾选某分组即表示把当前股票加入该分组。
/// 与 <see cref="GroupViewModel"/> 一一对应，<see cref="IsChecked"/> 为临时选择态（不入持久化）。
/// </summary>
public class AddTargetItem
{
    public GroupViewModel Group { get; set; } = null!;

    /// <summary>
    /// 勾选状态。**该分组已包含此股票时同样置为 true** —— 用于表达"它已经在这里面了"，
    /// 但此时 <see cref="CanCheck"/> 为 false，不允许用户改动（避免看起来还能重复添加一次）。
    /// </summary>
    public bool IsChecked { get; set; }

    /// <summary>该分组是否已包含此股票（弹窗打开时判定一次）</summary>
    public bool AlreadyInGroup { get; set; }

    /// <summary>能否勾选 / 取消勾选：已包含此股票的分组不可操作</summary>
    public bool CanCheck => !AlreadyInGroup;

    /// <summary>行内小字提示：已包含时显示「已添加」，否则空串（宽度为 0，不占位）</summary>
    public string Hint => AlreadyInGroup ? "已添加" : string.Empty;
}
