using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace FlowLens;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool PersistStats { get; set; } = true;
    public bool HideIdleRows { get; set; }
    public bool UseBitsPerSecond { get; set; }
    public bool ExcludeLocalTraffic { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 1;
    public ulong MinimumVisibleBytes { get; set; }
    public string Language { get; set; } = Localizer.NormalizeLanguage(null);
    public TrafficTimeRange TimeRange { get; set; } = TrafficTimeRange.Session;
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool ShowPidColumn { get; set; } = true;
    public bool ShowRateColumns { get; set; } = true;
    public bool ShowTotalColumns { get; set; } = true;
    public bool ShowIpSplitColumns { get; set; } = true;
    public bool ShowProtocolColumns { get; set; } = true;
    public bool ShowFlowsColumn { get; set; } = true;
    public bool ShowPathColumn { get; set; } = true;

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowLens");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.RefreshIntervalSeconds = Math.Clamp(settings.RefreshIntervalSeconds, 1, 10);
                    settings.Language = Localizer.NormalizeLanguage(settings.Language);
                    return settings;
                }
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
        ApplyStartupRegistration();
    }

    public void ApplyStartupRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null)
        {
            return;
        }

        if (StartWithWindows)
        {
            key.SetValue("FlowLens", $"\"{Environment.ProcessPath}\" --minimized");
        }
        else
        {
            key.DeleteValue("FlowLens", false);
        }
    }
}
