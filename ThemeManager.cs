using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace FlowLens;

public static class ThemeManager
{
    public static bool IsDark(AppSettings settings) => Resolve(settings.Theme) == AppTheme.Dark;

    public static void ApplyApplication(AppSettings settings)
    {
    }

    public static void Apply(Window window, AppSettings settings)
    {
        Apply(window, Resolve(settings.Theme));
    }

    public static void Apply(Window window, AppTheme theme)
    {
        var dark = Resolve(theme) == AppTheme.Dark;
        window.Background = Brush(dark ? "#0F172A" : "#F5F7FB");
        SetBrush(window, "PanelBrush", dark ? "#172033" : "#FFFFFF");
        SetBrush(window, "LineBrush", dark ? "#2A3750" : "#CBD5E1");
        SetBrush(window, "TextBrush", dark ? "#E5EDF7" : "#111827");
        SetBrush(window, "MutedBrush", dark ? "#9AABC1" : "#526174");
        SetBrush(window, "InputBrush", dark ? "#101827" : "#FFFFFF");
        SetBrush(window, "ButtonBrush", dark ? "#26354D" : "#E8EEF7");
        SetBrush(window, "ButtonBorderBrush", dark ? "#344761" : "#CBD5E1");
        SetBrush(window, "GridRowBrush", dark ? "#141D2F" : "#FFFFFF");
        SetBrush(window, "GridAltRowBrush", dark ? "#172238" : "#F1F5F9");
        SetBrush(window, "GridHeaderBrush", dark ? "#1E2A40" : "#E8EEF7");
        SetBrush(window, "ComboPanelBrush", dark ? "#101827" : "#FFFFFF");
        SetBrush(window, "ComboTextBrush", dark ? "#E5EDF7" : "#111827");
        SetBrush(window, "ComboHoverBrush", dark ? "#26354D" : "#E8F1FF");
    }

    public static AppTheme Resolve(AppTheme theme)
    {
        return theme == AppTheme.System
            ? (IsWindowsAppThemeLight() ? AppTheme.Light : AppTheme.Dark)
            : theme;
    }

    private static bool IsWindowsAppThemeLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (key?.GetValue("AppsUseLightTheme") as int? ?? 1) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void SetBrush(Window window, string key, string color)
    {
        SetBrush(window.Resources, key, color);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        var parsed = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = parsed;
            return;
        }

        resources[key] = new SolidColorBrush(parsed);
    }

    private static SolidColorBrush Brush(string color) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
