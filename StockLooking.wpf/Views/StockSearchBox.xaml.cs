using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StockLooking.wpf.Services;

// 歧义消解（项目同时 UseWPF + UseWindowsForms，隐式 using 会引入 System.Windows.Forms 同名类型）
using WMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace StockLooking.wpf.Views;

/// <summary>
/// 复用「盯盘设置」输入框的搜索能力：输入中文名/拼音/代码 → 腾讯智能提示 → 候选列表。
/// 设计要点（与 MainWindow.NewStockBox 一致）：
/// - 300ms 防抖：停止输入后才真正请求，避免逐字打接口；
/// - 请求序号：过期响应丢弃，避免乱序覆盖（慢请求晚回覆盖快请求结果）。
/// 对外暴露 <see cref="StockPicked"/>（点选/回车选中候选）与 <see cref="Text"/> / <see cref="Clear"/>。
/// </summary>
public partial class StockSearchBox : System.Windows.Controls.UserControl
{
    /// <summary>用户选中一个候选股票（来自候选列表点选或回车）</summary>
    public event EventHandler<StockSearchItem>? StockPicked;

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _searchSeq;
    private bool _suppress;
    private StockSearchItem? _lastPicked;

    public StockSearchBox()
    {
        InitializeComponent();
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await DoSearchAsync();
        };
    }

    /// <summary>当前输入文本（只读用途；清空请调用 <see cref="Clear"/>）</summary>
    public string Text => InputBox.Text;

    public void Clear()
    {
        _suppress = true;
        InputBox.Clear();
        _suppress = false;
        CandidatePopup.IsOpen = false;
        CandidateList.ItemsSource = null;
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void InputBox_KeyDown(object sender, WKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 回车：优先使用当前高亮候选，否则尝试按输入文本解析
            if (CandidateList.SelectedItem is StockSearchItem sel)
            {
                Emit(sel);
            }
            else if (!string.IsNullOrWhiteSpace(InputBox.Text))
            {
                _ = TryResolveAndEmitAsync(InputBox.Text.Trim());
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CandidatePopup.IsOpen = false;
        }
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中即准备提交；真正提交在 MouseUp/回车，避免单击误触发
    }

    private void CandidateList_MouseUp(object sender, WMouseEventArgs e)
    {
        if (CandidateList.SelectedItem is StockSearchItem sel) Emit(sel);
    }

    private async System.Threading.Tasks.Task TryResolveAndEmitAsync(string text)
    {
        var item = await ResolveAsync(text);
        if (item != null) Emit(item);
    }

    /// <summary>把输入文本解析为候选：纯代码直接规范化；否则按关键字搜索取首个结果</summary>
    private async System.Threading.Tasks.Task<StockSearchItem?> ResolveAsync(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        // 形如 600519 / sh600519 / sz000001 视为代码
        var digits = t.StartsWith("sh", StringComparison.OrdinalIgnoreCase) || t.StartsWith("sz", StringComparison.OrdinalIgnoreCase)
            ? t.Substring(2)
            : t;
        if (digits.Length > 0 && digits.All(char.IsDigit))
            return new StockSearchItem { Code = TencentStockClient.NormalizeCode(t), Name = TencentStockClient.NormalizeCode(t) };

        try
        {
            var items = await StockSearchService.SearchAsync(t);
            return items.Count > 0 ? items[0] : null;
        }
        catch { return null; }
    }

    private async System.Threading.Tasks.Task DoSearchAsync()
    {
        var kw = InputBox.Text.Trim();
        if (kw.Length == 0)
        {
            CandidatePopup.IsOpen = false;
            CandidateList.ItemsSource = null;
            return;
        }

        var seq = ++_searchSeq;
        List<StockSearchItem> items;
        try { items = await StockSearchService.SearchAsync(kw); }
        catch { items = new List<StockSearchItem>(); }

        if (seq != _searchSeq) return;   // 过期响应丢弃
        CandidateList.ItemsSource = items;
        CandidatePopup.IsOpen = items.Count > 0;
    }

    private void Emit(StockSearchItem item)
    {
        _lastPicked = item;
        StockPicked?.Invoke(this, item);
        Clear();
    }

    /// <summary>最近一次被选中的候选（供宿主在弹窗前预填）</summary>
    public StockSearchItem? LastPicked => _lastPicked;
}
