using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using AppThemeMode = LanTransfer.Platform.Windows.Settings.ThemeMode;

namespace LanTransfer.App.Themes;

/// <summary>浅色 / 深色 / 跟随系统主题管理器，并同步 Windows 10/11 原生沉浸式标题栏颜色。</summary>
public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private static ResourceDictionary? _currentTheme;

    public static bool IsDark { get; private set; }

    public static void Apply(AppThemeMode mode)
    {
        var dark = mode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => IsSystemDarkMode(),
        };

        IsDark = dark;
        var uri = new Uri(dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative);
        var next = new ResourceDictionary { Source = uri };
        var dicts = Application.Current.Resources.MergedDictionaries;
        if (_currentTheme is not null)
        {
            var idx = dicts.IndexOf(_currentTheme);
            if (idx >= 0)
            {
                dicts[idx] = next;
            }
            else
            {
                dicts.Insert(0, next);
            }
        }
        else
        {
            dicts.Insert(0, next);
        }

        _currentTheme = next;

        foreach (Window w in Application.Current.Windows)
        {
            ApplyTitleBar(w, dark);
        }
    }

    public static void ApplyTitleBar(Window window, bool? darkOverride = null)
    {
        var dark = darkOverride ?? IsDark;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyTitleBar(window);
            return;
        }

        var useDark = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
        }
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
