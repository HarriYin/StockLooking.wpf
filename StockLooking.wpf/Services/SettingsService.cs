using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

/// <summary>
/// 配置持久化（JSON）
/// </summary>
public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StockLooking", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, Options());
                if (s != null) return Normalize(s);
            }
        }
        catch
        {
            // 损坏则回退默认
        }
        return AppSettings.CreateDefault();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(s, Options());
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 忽略保存失败
        }
    }

    /// <summary>兜底修复 JSON 中的 null/越界字段，避免运行期空引用</summary>
    private static AppSettings Normalize(AppSettings s)
    {
        s.Stocks ??= new List<string>();
        // ★ 清掉非法代码（历史遗留：搜到美股时曾被 NormalizeCode 补成 `szusmpb.oq` 这类；
        //   腾讯对无效代码会返回 v_pv_none_match 哨兵，导致该条目名称显示成 "pv_none_match" 且永远加载失败）
        s.Stocks = s.Stocks.Where(TencentStockClient.IsAShareCode).Distinct().ToList();
        s.HiddenStocks ??= new List<string>();
        // 清理：仅保留仍存在于股票池中的隐藏项
        s.HiddenStocks = s.HiddenStocks.Where(c => s.Stocks.Contains(c)).Distinct().ToList();
        s.Fields ??= AppSettings.DefaultFields();
        if (s.Fields.Count == 0) s.Fields = AppSettings.DefaultFields();

        // 以默认字段顺序为基准合并已保存配置：旧配置文件里缺失的新字段（如「振幅」）会被自动补齐
        var saved = s.Fields.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());
        var merged = AppSettings.DefaultFields()
            .Select(def => saved.TryGetValue(def.Key, out var had) ? had : def)
            .ToList();
        foreach (var extra in s.Fields.Where(f => merged.All(m => m.Key != f.Key))) merged.Add(extra);
        s.Fields = merged;
        s.RefreshIntervalMs = Math.Max(1000, s.RefreshIntervalMs);
        s.KlineRefreshIntervalMs = Math.Clamp(s.KlineRefreshIntervalMs, 1000, 60000);
        s.MultiKlineRefreshIntervalMs = Math.Clamp(s.MultiKlineRefreshIntervalMs, 1000, 60000);
        // 概览页配置版本迁移：该值的历史默认值改过两次（60000 → 3000 → 1000），
        // 而它最初没有 UI、配置里的值全是程序写的 → 版本落后就重置为最新默认值（此后尊重用户调整）。
        if (s.OverviewConfigVersion < AppSettings.CurrentOverviewConfigVersion)
        {
            s.OverviewConfigVersion = AppSettings.CurrentOverviewConfigVersion;
            s.OverviewRefreshIntervalMs = AppSettings.OverviewDefaultRefreshIntervalMs;
        }
        s.OverviewRefreshIntervalMs = Math.Clamp(s.OverviewRefreshIntervalMs, 1000, 60000);
        if (!new[] { "intraday", "101", "102", "103" }.Contains(s.KlineType)) s.KlineType = "101";
        s.BackgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0, 1);
        s.TextOpacity = Math.Clamp(s.TextOpacity, 0.2, 1);   // 低于 0.2 几乎不可读，兜个下限
        s.FontSize = Math.Clamp(s.FontSize, 10, 36);
        s.BackgroundColor = string.IsNullOrWhiteSpace(s.BackgroundColor) ? "#000000" : s.BackgroundColor;
        s.ForegroundColor = string.IsNullOrWhiteSpace(s.ForegroundColor) ? "#FFFFFFFF" : s.ForegroundColor;
        if (!Enum.IsDefined(typeof(ThemeMode), s.Theme)) s.Theme = ThemeMode.System;

        // 分组：名称兜底、代码规范化、去重、单组不超过上限
        s.Groups ??= new List<StockGroup>();
        foreach (var g in s.Groups)
        {
            if (g == null) continue;
            if (string.IsNullOrWhiteSpace(g.Name)) g.Name = "分组";
            g.Codes = (g.Codes ?? new List<string>())
                .Select(TencentStockClient.NormalizeCode)
                .Where(TencentStockClient.IsAShareCode)
                .Distinct()
                .Take(StockGroup.MaxItems)
                .ToList();
        }
        // 剔除空分组定义（防御损坏配置）
        s.Groups = s.Groups.Where(g => g != null).ToList();

        // 多股K线容器：代码规范化 + 去重，隐藏项仅保留仍在容器内的
        s.MultiKlineStocks = (s.MultiKlineStocks ?? new List<string>())
            .Select(TencentStockClient.NormalizeCode)
            .Where(TencentStockClient.IsAShareCode)
            .Distinct()
            .ToList();
        s.MultiKlineHidden = (s.MultiKlineHidden ?? new List<string>())
            .Select(TencentStockClient.NormalizeCode)
            .Where(x => s.MultiKlineStocks.Contains(x))
            .Distinct()
            .ToList();
        // 注：曾经用于"明细页叠加大盘"的 DetailKlineIndexes / MultiKlineIndexes 已随该功能一并移除，
        // 旧配置里的这两个键会被 System.Text.Json 直接忽略（默认忽略未知成员），无需迁移。
        return s;
    }

    private static JsonSerializerOptions Options() => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
