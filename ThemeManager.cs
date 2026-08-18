using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FlowLens;

public static class ThemeManager
{
    private const string ThemeDictionaryPrefix = "Themes/Colors.";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private static AppTheme _resolvedTheme = AppTheme.Light;

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
        _resolvedTheme = resolved;
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

        foreach (Window window in application.Windows)
        {
            ApplyTitleBar(window, resolved);
        }
    }

    public static void Apply(Window window, AppSettings settings)
    {
        Apply(window, settings.Theme);
    }

    public static void Apply(Window window, AppTheme theme)
    {
        ApplyApplication(theme);
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        ApplyTitleBar(window, _resolvedTheme);
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

    private static void ApplyTitleBar(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized -= Window_SourceInitialized;
            window.SourceInitialized += Window_SourceInitialized;
            return;
        }

        SetImmersiveDarkMode(handle, theme == AppTheme.Dark);
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.SourceInitialized -= Window_SourceInitialized;
        ApplyTitleBar(window, _resolvedTheme);
    }

    private static void SetImmersiveDarkMode(IntPtr windowHandle, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var value = enabled ? 1 : 0;
            var result = DwmSetWindowAttribute(
                windowHandle,
                DwmUseImmersiveDarkMode,
                ref value,
                Marshal.SizeOf<int>());

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    windowHandle,
                    DwmUseImmersiveDarkModeLegacy,
                    ref value,
                    Marshal.SizeOf<int>());
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

}
