using System;
using System.Collections.Generic;
using System.Windows;

namespace StockLooking.wpf.Views;

/// <summary>
/// 导入分组时的重命名对话框：预填文件中的原名称，用户可以不改；
/// 但名称为空、或与现有分组重名时**不允许确定**（重名必须修改后才能导入）。
/// </summary>
public partial class GroupNameDialog : Window
{
    private readonly HashSet<string> _existing;

    /// <summary>用户最终确认的名称（已 Trim）</summary>
    public string GroupName => NameBox.Text.Trim();

    public GroupNameDialog(string defaultName, string hint, IEnumerable<string> existingNames)
    {
        InitializeComponent();
        _existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        HintText.Text = hint;
        NameBox.Text = defaultName;
        NameBox.SelectAll();
        NameBox.Focus();
        Validate();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var name = GroupName;
        if (name.Length == 0)
        {
            SetError("名称不能为空。");
            return;
        }
        if (_existing.Contains(name))
        {
            SetError($"「{name}」已存在，请修改后再导入。");
            return;
        }
        SetError(null);
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        OkBtn.IsEnabled = message == null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OkBtn.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
