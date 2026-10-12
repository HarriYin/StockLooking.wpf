using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

// 歧义消解：隐式 using 引入了 System.Drawing / System.Windows.Forms，与 WPF 同名类型冲突
using MediaColor = System.Windows.Media.Color;
using WButton = System.Windows.Controls.Button;

namespace StockLooking.wpf.Views;

/// <summary>
/// 通用对话框（Apple 风），用于替代系统 <c>MessageBox</c> ——
/// 系统 MessageBox 是 Windows 经典外观（直角、浅色、字体不一致），与本项目整体风格不符。
///
/// 用法：
/// <code>
/// AppDialog.Info("已导出 3 个分组。");
/// AppDialog.Error("导出失败：…");
/// if (!AppDialog.Confirm("删除分组「CRO」？")) return;
/// </code>
/// </summary>
public partial class AppDialog : Window
{
    private enum Kind { Info, Error, Confirm }

    private AppDialog() => InitializeComponent();

    /// <summary>信息提示（单按钮「确定」）</summary>
    public static void Info(string message, string title = "提示") => Show(message, title, Kind.Info);

    /// <summary>错误提示（单按钮「确定」，红色图标）</summary>
    public static void Error(string message, string title = "出错了") => Show(message, title, Kind.Error);

    /// <summary>确认框：返回 true 表示用户点了「确定」</summary>
    public static bool Confirm(string message, string title = "确认") => Show(message, title, Kind.Confirm);

    private static bool Show(string message, string title, Kind kind)
    {
        var dlg = new AppDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;

        var (symbol, color) = kind switch
        {
            Kind.Error => ("!", MediaColor.FromRgb(0xFF, 0x44, 0x38)),
            Kind.Confirm => ("?", MediaColor.FromRgb(0x0A, 0x84, 0xFF)),
            _ => ("i", MediaColor.FromRgb(0x0A, 0x84, 0xFF))
        };
        dlg.IconBd.Background = new SolidColorBrush(color);
        dlg.IconText.Text = symbol;

        if (kind == Kind.Confirm)
        {
            var cancel = new WButton { Content = "取消", Margin = new Thickness(0, 0, 10, 0) };
            cancel.Click += (_, _) => dlg.DialogResult = false;
            var ok = new WButton { Content = "确定", IsDefault = true };
            ok.Click += (_, _) => dlg.DialogResult = true;
            dlg.ButtonPanel.Children.Add(cancel);
            dlg.ButtonPanel.Children.Add(ok);
        }
        else
        {
            // 提示类：Esc / Enter 都当作「确定」关闭（不用 IsCancel，避免与 DialogResult 赋值冲突）
            var ok = new WButton { Content = "确定", IsDefault = true };
            ok.Click += (_, _) => dlg.DialogResult = true;
            dlg.ButtonPanel.Children.Add(ok);
        }

        // Esc 关闭：确认框按「取消」处理，提示类直接关闭
        dlg.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            dlg.DialogResult = kind != Kind.Confirm;
        };

        return dlg.ShowDialog() == true;
    }

    /// <summary>整块可拖动（无边框窗口），拖动失败不影响使用</summary>
    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* 忽略 */ }
    }
}
