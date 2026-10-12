using System.Collections.Generic;

namespace StockLooking.wpf.Models;

/// <summary>
/// 自定义分组（同花顺式自选分组）。
/// 组内只存股票代码；名称/行情由实时数据填充。
/// 单组上限 <see cref="MaxItems"/> 项，由 SettingsService 归一化与界面添加逻辑共同保证。
/// </summary>
public class StockGroup
{
    /// <summary>分组显示名（可重命名）</summary>
    public string Name { get; set; } = "新分组";

    /// <summary>组内股票代码（带市场前缀，如 sh600519）</summary>
    public List<string> Codes { get; set; } = new();

    /// <summary>单组最大股票数</summary>
    public const int MaxItems = 100;
}
