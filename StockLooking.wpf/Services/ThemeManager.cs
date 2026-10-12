using System;
using System.Windows;
using Microsoft.Win32;
using StockLooking.wpf.Models;

namespace StockLooking.wpf.Services;

/// <summary>
/// 主题管理：在浅色 / 深色资源字典之间切换应用级 MergedDictionaries。
/// 所有窗口样式通过 DynamicResource 引用共享 Brush 键，切换即实时换肤。
/// </summary>
public static class ThemeManager
{
    private const string ThemeFolder = "/Themes/";
    private const string LightUri = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkUri = "pack://application:,,,/Themes/Dark.xaml";

    private static ThemeMode _current = ThemeMode.System;

    /// <summary>当前生效的主题（System 模式下返回实际解析结果）</summary>
    public static ThemeMode Current => _current == ThemeMode.System ? GetSystemTheme() : _current;

    /// <summary>应用指定主题；System 会读取系统设置</summary>
    public static void Apply(ThemeMode mode)
    {
        _current = mode;
        var actual = mode == ThemeMode.System ? GetSystemTheme() : mode;
        var uri = actual == ThemeMode.Dark ? DarkUri : LightUri;

        var app = System.Windows.Application.Current;
        if (app == null) return;

        // 移除旧的 Light/Dark 字典，保留其它（避免重复叠加）
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var src = app.Resources.MergedDictionaries[i].Source?.OriginalString;
            if (src != null && src.Contains(ThemeFolder))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }

        app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
    }

    /// <summary>读取 Windows 个性化中的「应用主题」：0=深色 1=浅色</summary>
    public static ThemeMode GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0 ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch
        {
            // 读取失败回退浅色
        }
        return ThemeMode.Light;
    }
}
