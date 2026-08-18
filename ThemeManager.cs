using Microsoft.Win32;
using System.Windows;

namespace FlowLens;

public static class ThemeManager
{
    private const string ThemeDictionaryPrefix = "Themes/Colors.";

    public static bool IsDark(AppSettings settings) => Resolve(settings.Theme) == AppTheme.Dark;

    public static void ApplyApplication(AppSettings settings)
    {
        ApplyApplication(settings.Theme);
    }

    public static void ApplyApplication(AppTheme theme)
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        var resolved = Resolve(theme);
        var source = new Uri(
            resolved == AppTheme.Dark
                ? "Themes/Colors.Dark.xaml"
                : "Themes/Colors.Light.xaml",
            UriKind.Relative);
        var dictionaries = application.Resources.MergedDictionaries;
        var existingIndex = -1;
        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Source?.OriginalString.Contains(ThemeDictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                existingIndex = index;
                break;
            }
        }

        var replacement = new ResourceDictionary { Source = source };
        if (existingIndex >= 0)
        {
            dictionaries[existingIndex] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }
    }

    public static void Apply(Window window, AppSettings settings)
    {
        Apply(window, Resolve(settings.Theme));
    }

    public static void Apply(Window window, AppTheme theme)
    {
        ApplyApplication(theme);
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
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

}
