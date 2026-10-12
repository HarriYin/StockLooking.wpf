using System;
using System.Collections.Generic;

namespace StockLooking.wpf.Services;

/// <summary>MACD 计算结果：与传入的 K 线序列一一对应（下标一致）</summary>
public sealed class MacdResult
{
    /// <summary>DIF 快线 = EMA(CLOSE,12) − EMA(CLOSE,26)</summary>
    public List<double> Dif { get; } = new();

    /// <summary>DEA 慢线（信号线）= EMA(DIF,9)</summary>
    public List<double> Dea { get; } = new();

    /// <summary>MACD 柱 = (DIF − DEA) × 2</summary>
    public List<double> Hist { get; } = new();

    public int Count => Dif.Count;

    /// <summary>取 [fromIndex, 末尾] 的一段（用于「预热序列 + 目标序列」整体递推后只保留目标段）</summary>
    public MacdResult Slice(int fromIndex)
    {
        var r = new MacdResult();
        for (var i = Math.Max(0, fromIndex); i < Count; i++)
        {
            r.Dif.Add(Dif[i]);
            r.Dea.Add(Dea[i]);
            r.Hist.Add(Hist[i]);
        }
        return r;
    }
}

/// <summary>
/// MACD 指标计算（通达信标准口径）。
///
/// ⚠ MACD 是技术指标、不是行情数据：任何行情接口都不直接返回它，必须由本地用**收盘价序列**计算。
/// 我们已有的 K 线接口（东财 push2his / 腾讯 fqkline / 新浪）都返回 OHLCV，所以数据完全够用。
///
/// 公式：
///   DIF = EMA(CLOSE,12) − EMA(CLOSE,26)
///   DEA = EMA(DIF,9)
///   MACD 柱 = (DIF − DEA) × 2
/// EMA 递推：Y_today = X_today × 2/(n+1) + Y_yesterday × (n−1)/(n+1)，首值取序列首值（与通达信一致）。
///
/// 数据量：最少需要 26 + 9 = 35 根；经验上 100 根以上 EMA 才充分收敛（项目默认 KlineLimit=120）。
/// </summary>
public static class MacdCalculator
{
    public static MacdResult Compute(IList<KLineItem>? items, int fast = 12, int slow = 26, int signal = 9)
    {
        if (items == null || items.Count == 0) return new MacdResult();

        var closes = new double[items.Count];
        for (var i = 0; i < items.Count; i++) closes[i] = (double)items[i].Close;
        return FromCloses(closes, fast, slow, signal);
    }

    /// <summary>
    /// 带预热序列的 MACD：先用 warmup 喂 EMA，再算目标序列，最后只返回与 items 下标对齐的那一段。
    ///
    /// 为什么需要：分时图只有当日约 240 根，若直接从 09:30 起算，EMA(12)/EMA(26) 的首值都取序列首值，
    /// 开盘后约半小时的 DIF/DEA 会明显偏离「连续分钟 K 线」口径（表现为开盘段 MACD 失真）。
    /// 拼上最近数个交易日的分钟收盘价后 EMA 充分收敛，结果与通达信「1 分钟 MACD」一致。
    /// warmup 仅参与递推，不进入返回结果；warmup 为空时等价于 <see cref="Compute"/>。
    /// </summary>
    public static MacdResult ComputeWithWarmup(IList<KLineItem>? warmup, IList<KLineItem>? items,
                                               int fast = 12, int slow = 26, int signal = 9)
    {
        if (items == null || items.Count == 0) return new MacdResult();
        var wn = warmup?.Count ?? 0;
        if (wn == 0) return Compute(items, fast, slow, signal);

        var closes = new double[wn + items.Count];
        for (var i = 0; i < wn; i++) closes[i] = (double)warmup![i].Close;
        for (var i = 0; i < items.Count; i++) closes[wn + i] = (double)items[i].Close;
        return FromCloses(closes, fast, slow, signal).Slice(wn);
    }

    /// <summary>由收盘价序列计算 MACD（EMA 递推：Y_today = X·2/(n+1) + Y_yesterday·(n−1)/(n+1)，首值取序列首值）</summary>
    private static MacdResult FromCloses(double[] closes, int fast, int slow, int signal)
    {
        var result = new MacdResult();
        var n = closes.Length;
        if (n == 0) return result;

        var emaFast = Ema(closes, fast);
        var emaSlow = Ema(closes, slow);

        var dif = new double[n];
        for (var i = 0; i < n; i++) dif[i] = emaFast[i] - emaSlow[i];
        var dea = Ema(dif, signal);

        for (var i = 0; i < n; i++)
        {
            result.Dif.Add(dif[i]);
            result.Dea.Add(dea[i]);
            result.Hist.Add((dif[i] - dea[i]) * 2);
        }
        return result;
    }

    private static double[] Ema(double[] src, int period)
    {
        var k = 2.0 / (period + 1);
        var outArr = new double[src.Length];
        outArr[0] = src[0];
        for (var i = 1; i < src.Length; i++)
            outArr[i] = src[i] * k + outArr[i - 1] * (1 - k);
        return outArr;
    }
}
