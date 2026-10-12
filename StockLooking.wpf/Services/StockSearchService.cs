using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StockLooking.wpf.Services;

/// <summary>
/// 股票搜索统一入口：本地全量名录（子串匹配，任意位置可命中） ∪ 腾讯 smartbox（前缀/拼音提示）。
///
/// 合并策略：**本地结果优先**，smartbox 只补充本地没有的代码 ——
/// 既保证「输入任意字（含名称中间字）都能命中」，又保留 smartbox 的拼音缩写/前缀能力。
/// 全部搜索框（盯盘设置「添加股票」、「分组自选」搜索、多股K线容器搜索）统一走这里。
/// </summary>
public static class StockSearchService
{
    /// <summary>候选上限（过多无意义，且下拉列表越长越难点）</summary>
    private const int MaxResults = 12;

    /// <summary>
    /// 按关键字搜索候选（中文名称任意位置 / 代码任意位置 / smartbox 前缀与拼音）。
    /// 任何数据源失败都不抛异常，只返回已拿到的部分。
    /// </summary>
    public static async Task<List<StockSearchItem>> SearchAsync(string keyword)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return new List<StockSearchItem>();

        // 本地名录（子串匹配）—— 首次调用会触发加载/拉取，故启动时应先预热
        await StockCatalog.EnsureLoadedAsync().ConfigureAwait(false);

        // 顺带检查名录是否"够新"：过期则后台静默刷新（fire-and-forget，不阻塞本次搜索）。
        // 这样即便程序长年不重启，只要用户在用搜索，新上市的票也会被及时收录。
        _ = StockCatalog.RefreshIfStaleAsync(StockCatalog.RefreshAge);

        var merged = new List<StockSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in StockCatalog.Search(kw))
        {
            if (seen.Add(DedupKey(it.Code))) merged.Add(it);
            if (merged.Count >= MaxResults) break;
        }

        // smartbox 补充（拼音缩写、尚未收录的新股等）
        if (merged.Count < MaxResults)
        {
            try
            {
                foreach (var it in await TencentStockClient.SearchAsync(kw).ConfigureAwait(false))
                {
                    if (seen.Add(DedupKey(it.Code))) merged.Add(it);
                    if (merged.Count >= MaxResults) break;
                }
            }
            catch { /* 提示接口失败不影响本地结果 */ }
        }

        return merged;
    }

    /// <summary>
    /// 去重键：**去掉代码里的分隔点**再比较。
    /// 本地名录产出 <c>sz002281</c>、腾讯 smartbox 产出 <c>sz.002281</c>，
    /// 字符串不同但确实是同一只票 —— 直接比 Code 会让同一只票在候选里出现两次（重复项）。
    /// </summary>
    private static string DedupKey(string code)
        => (code ?? "").Replace(".", "").Trim();
}
