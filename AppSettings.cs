using System.IO;
using System.Diagnostics;
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
    public string NetworkInterfaceId { get; set; } = string.Empty;
    public int RefreshIntervalSeconds { get; set; } = 1;
    public ulong MinimumVisibleBytes { get; set; }
    public string Language { get; set; } = Localizer.NormalizeLanguage(null);
    public TrafficTimeRange TimeRange { get; set; } = TrafficTimeRange.Session;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool ShowPidColumn { get; set; }
    public bool ShowRateColumns { get; set; } = true;
    public bool ShowTotalColumns { get; set; } = true;
    public bool ShowIpSplitColumns { get; set; } = true;
    public bool ShowProtocolColumns { get; set; }
    public bool ShowFlowsColumn { get; set; } = true;
    public bool ShowPathColumn { get; set; }

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
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(SettingsPath, json);
    }

    public StartupRegistrationResult ApplyStartupRegistration()
    {
        try
        {
            ClearRunRegistryRegistration();

            return StartWithWindows
                ? CreateStartupTask()
                : DeleteStartupTask();
        }
        catch (Exception ex)
        {
            return StartupRegistrationResult.Failure(ex.Message);
        }
    }

    private static void ClearRunRegistryRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null)
        {
            return;
        }

        key.DeleteValue("FlowLens", false);
    }

    private static StartupRegistrationResult CreateStartupTask()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return StartupRegistrationResult.Failure("The executable path is unavailable.");
        }

        return RunSchtasks(
            "/Create",
            "/TN", "FlowLens",
            "/SC", "ONLOGON",
            "/TR", $"\"{executablePath}\" --minimized",
            "/RL", "HIGHEST",
            "/F");
    }

    private static StartupRegistrationResult DeleteStartupTask()
    {
        var query = RunSchtasks("/Query", "/TN", "FlowLens");
        return query.Succeeded
            ? RunSchtasks("/Delete", "/TN", "FlowLens", "/F")
            : StartupRegistrationResult.Success;
    }

    private static StartupRegistrationResult RunSchtasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return StartupRegistrationResult.Failure("Unable to start schtasks.exe.");
            }

            var standardErrorTask = process.StandardError.ReadToEndAsync();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return StartupRegistrationResult.Failure("Task Scheduler did not respond within 10 seconds.");
            }

            var output = standardErrorTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(output))
            {
                output = standardOutputTask.GetAwaiter().GetResult();
            }

            return process.ExitCode == 0
                ? StartupRegistrationResult.Success
                : StartupRegistrationResult.Failure(output.Trim());
        }
        catch (Exception ex)
        {
            return StartupRegistrationResult.Failure(ex.Message);
        }
    }
}

public sealed record StartupRegistrationResult(bool Succeeded, string ErrorMessage)
{
    public static StartupRegistrationResult Success { get; } = new(true, string.Empty);
    public static StartupRegistrationResult Failure(string message) => new(false, message);
}
