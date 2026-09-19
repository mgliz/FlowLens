using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FlowLens;

public sealed class AppSettings
{
    internal const int CurrentStartupTaskVersion = 2;
    private const string StartupTaskName = "FlowLens";

    public bool StartWithWindows { get; set; }
    public int StartupTaskVersion { get; set; }
    public string StartupExecutablePath { get; set; } = string.Empty;
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
    public DateTime CustomRangeStart { get; set; } = DateTime.Today;
    public DateTime CustomRangeEnd { get; set; } = DateTime.Today;
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
                    if (!Enum.IsDefined(settings.TimeRange))
                        settings.TimeRange = TrafficTimeRange.Session;
                    if (settings.CustomRangeStart.Date > settings.CustomRangeEnd.Date ||
                        settings.CustomRangeEnd.Date > DateTime.Today)
                        settings.CustomRangeStart = settings.CustomRangeEnd = DateTime.Today;
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

            var executablePath = Environment.ProcessPath;
            var result = StartWithWindows
                ? CreateStartupTask(executablePath)
                : DeleteStartupTask();

            if (result.Succeeded)
            {
                StartupTaskVersion = StartWithWindows ? CurrentStartupTaskVersion : 0;
                StartupExecutablePath = StartWithWindows ? executablePath ?? string.Empty : string.Empty;
            }

            return result;
        }
        catch (Exception ex)
        {
            return StartupRegistrationResult.Failure(ex.Message);
        }
    }

    public bool StartupRegistrationNeedsRefresh()
    {
        if (!StartWithWindows)
        {
            return false;
        }

        if (!IsStartupMetadataCurrent(StartupTaskVersion, StartupExecutablePath, Environment.ProcessPath))
        {
            return true;
        }

        return !RunSchtasks("/Query", "/TN", StartupTaskName).Succeeded;
    }

    internal static bool IsStartupMetadataCurrent(int version, string registeredPath, string? currentPath)
    {
        return version == CurrentStartupTaskVersion &&
               !string.IsNullOrWhiteSpace(currentPath) &&
               string.Equals(registeredPath, currentPath, StringComparison.OrdinalIgnoreCase);
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

    private static StartupRegistrationResult CreateStartupTask(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return StartupRegistrationResult.Failure("The executable path is unavailable.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return StartupRegistrationResult.Failure("The current Windows user SID is unavailable.");
        }

        var taskDocument = BuildStartupTaskDocument(executablePath, userSid, identity.Name);
        var taskFile = Path.Combine(Path.GetTempPath(), $"FlowLens-startup-{Guid.NewGuid():N}.xml");

        try
        {
            var writerSettings = new XmlWriterSettings
            {
                Encoding = Encoding.Unicode,
                Indent = true,
                OmitXmlDeclaration = false
            };
            using (var writer = XmlWriter.Create(taskFile, writerSettings))
            {
                taskDocument.Save(writer);
            }

            return RunSchtasks(
                "/Create",
                "/TN", StartupTaskName,
                "/XML", taskFile,
                "/F");
        }
        finally
        {
            try
            {
                File.Delete(taskFile);
            }
            catch
            {
            }
        }
    }

    internal static XDocument BuildStartupTaskDocument(string executablePath, string userSid, string author)
    {
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

        return new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(task + "Task",
                new XAttribute("version", "1.2"),
                new XElement(task + "RegistrationInfo",
                    new XElement(task + "Author", author),
                    new XElement(task + "Description", "Starts FlowLens shortly after this user signs in.")),
                new XElement(task + "Principals",
                    new XElement(task + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(task + "UserId", userSid),
                        new XElement(task + "LogonType", "InteractiveToken"),
                        new XElement(task + "RunLevel", "HighestAvailable"))),
                new XElement(task + "Settings",
                    new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(task + "DisallowStartIfOnBatteries", "false"),
                    new XElement(task + "StopIfGoingOnBatteries", "false"),
                    new XElement(task + "AllowHardTerminate", "true"),
                    new XElement(task + "StartWhenAvailable", "true"),
                    new XElement(task + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(task + "AllowStartOnDemand", "true"),
                    new XElement(task + "Enabled", "true"),
                    new XElement(task + "Hidden", "false"),
                    new XElement(task + "RunOnlyIfIdle", "false"),
                    new XElement(task + "WakeToRun", "false"),
                    new XElement(task + "ExecutionTimeLimit", "PT0S"),
                    new XElement(task + "Priority", "7")),
                new XElement(task + "Triggers",
                    new XElement(task + "LogonTrigger",
                        new XElement(task + "Enabled", "true"),
                        new XElement(task + "UserId", userSid),
                        new XElement(task + "Delay", "PT10S"))),
                new XElement(task + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(task + "Exec",
                        new XElement(task + "Command", executablePath),
                        new XElement(task + "Arguments", "--minimized"),
                        new XElement(task + "WorkingDirectory", workingDirectory)))));
    }

    private static StartupRegistrationResult DeleteStartupTask()
    {
        var query = RunSchtasks("/Query", "/TN", StartupTaskName);
        return query.Succeeded
            ? RunSchtasks("/Delete", "/TN", StartupTaskName, "/F")
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
