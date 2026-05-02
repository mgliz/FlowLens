using System.Globalization;

namespace FlowLens;

public static class Localizer
{
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = new()
    {
        ["en-US"] = new Dictionary<string, string>
        {
            ["AppSubtitle"] = "Per-process TCP / UDP traffic with IPv4 / IPv6 split",
            ["SearchPlaceholder"] = "Search process, PID, or path",
            ["Settings"] = "Settings",
            ["About"] = "About",
            ["AboutTitle"] = "About FlowLens",
            ["AboutSubtitle"] = "Lightweight Windows per-process traffic monitor.",
            ["AboutVersion"] = "Version",
            ["AboutFeatures"] = "IPv4 / IPv6, TCP / UDP, per-process rates, local statistics, tray mode, and theme switching.",
            ["AboutRuntime"] = ".NET 8 Windows desktop app. Administrator privileges are required for ETW capture.",
            ["AboutData"] = "Local data",
            ["AboutLicense"] = "License",
            ["AboutLicenseValue"] = "MIT License",
            ["AboutAi"] = "This project was completed with AI assistance.",
            ["Close"] = "Close",
            ["Reset"] = "Reset",
            ["TotalCurrent"] = "Total / current",
            ["ProcessesFlows"] = "Processes / flows",
            ["Process"] = "Process",
            ["Rate"] = "Rate",
            ["Ipv4Rate"] = "IPv4 rate",
            ["Ipv6Rate"] = "IPv6 rate",
            ["Received"] = "Received",
            ["Sent"] = "Sent",
            ["Ipv4Rs"] = "IPv4 R/S",
            ["Ipv6Rs"] = "IPv6 R/S",
            ["TcpRs"] = "TCP R/S",
            ["UdpRs"] = "UDP R/S",
            ["Flows"] = "Flows",
            ["Path"] = "Path",
            ["Ready"] = "Ready",
            ["Collecting"] = "Collecting",
            ["NotAdmin"] = "Not running as administrator; ETW capture may fail",
            ["CaptureWarning"] = "Capture warning",
            ["ResetComplete"] = "Reset complete; waiting for traffic",
            ["TrayHint"] = "ETW Kernel Network · TCP + UDP · Administrator required",
            ["RangeSession"] = "Session",
            ["RangeToday"] = "Today",
            ["Range7Days"] = "Last 7 days",
            ["Range30Days"] = "Last 30 days",
            ["RangeAll"] = "All",
            ["SettingsTitle"] = "Settings",
            ["SettingsSubtitle"] = "Startup, tray behavior, refresh, language, and local statistics.",
            ["StartWithWindows"] = "Start with Windows",
            ["StartMinimized"] = "Start minimized to tray",
            ["CloseToTray"] = "Close button minimizes to tray",
            ["AlwaysOnTop"] = "Keep window always on top",
            ["PersistStats"] = "Save statistics locally",
            ["HideIdleRows"] = "Hide rows below minimum total bytes",
            ["UseBitsPerSecond"] = "Show rates as bit/s",
            ["ExcludeLocalTraffic"] = "Exclude local loopback traffic",
            ["RefreshInterval"] = "Refresh interval (1-10 seconds)",
            ["MinimumBytes"] = "Minimum visible total bytes",
            ["Language"] = "Language",
            ["StartupNote"] = "Startup uses Windows Task Scheduler with highest privileges. The task is created or removed when you save settings.",
            ["Theme"] = "Theme",
            ["ThemeSystem"] = "Follow system",
            ["ThemeDark"] = "Dark",
            ["ThemeLight"] = "Light",
            ["VisibleColumns"] = "Visible columns",
            ["ShowPidColumn"] = "PID",
            ["ShowRateColumns"] = "Rate columns",
            ["ShowTotalColumns"] = "Total received/sent",
            ["ShowIpSplitColumns"] = "IPv4 / IPv6 split",
            ["ShowProtocolColumns"] = "TCP / UDP split",
            ["ShowFlowsColumn"] = "Flows",
            ["ShowPathColumn"] = "Path",
            ["ResetStatistics"] = "Reset statistics",
            ["ResetWarning"] = "This will permanently clear all locally saved FlowLens traffic statistics. Continue?",
            ["ResetDone"] = "Statistics were reset.",
            ["Cancel"] = "Cancel",
            ["Save"] = "Save",
            ["Show"] = "Show",
            ["Exit"] = "Exit"
        },
        ["zh-CN"] = new Dictionary<string, string>
        {
            ["AppSubtitle"] = "按进程统计 TCP / UDP 流量，并区分 IPv4 / IPv6",
            ["SearchPlaceholder"] = "搜索进程、PID 或路径",
            ["Settings"] = "设置",
            ["About"] = "关于",
            ["AboutTitle"] = "关于 FlowLens",
            ["AboutSubtitle"] = "轻量级 Windows 按进程流量监控工具。",
            ["AboutVersion"] = "版本",
            ["AboutFeatures"] = "IPv4 / IPv6、TCP / UDP、按进程实时速率、本地统计、托盘常驻和主题切换。",
            ["AboutRuntime"] = ".NET 8 Windows 桌面应用。ETW 采集需要管理员权限。",
            ["AboutData"] = "本地数据",
            ["AboutLicense"] = "开源协议",
            ["AboutLicenseValue"] = "MIT License",
            ["AboutAi"] = "本项目由 AI 辅助完成。",
            ["Close"] = "关闭",
            ["Reset"] = "重置",
            ["TotalCurrent"] = "总量 / 当前",
            ["ProcessesFlows"] = "进程 / 流",
            ["Process"] = "进程",
            ["Rate"] = "速率",
            ["Ipv4Rate"] = "IPv4 速率",
            ["Ipv6Rate"] = "IPv6 速率",
            ["Received"] = "接收",
            ["Sent"] = "发送",
            ["Ipv4Rs"] = "IPv4 收/发",
            ["Ipv6Rs"] = "IPv6 收/发",
            ["TcpRs"] = "TCP 收/发",
            ["UdpRs"] = "UDP 收/发",
            ["Flows"] = "流",
            ["Path"] = "路径",
            ["Ready"] = "准备就绪",
            ["Collecting"] = "正在采集",
            ["NotAdmin"] = "未以管理员权限运行，ETW 采集可能失败",
            ["CaptureWarning"] = "采集警告",
            ["ResetComplete"] = "已重置，等待新流量",
            ["TrayHint"] = "ETW Kernel Network · TCP + UDP · 需要管理员权限",
            ["RangeSession"] = "本次运行",
            ["RangeToday"] = "今天",
            ["Range7Days"] = "最近 7 天",
            ["Range30Days"] = "最近 30 天",
            ["RangeAll"] = "全部",
            ["SettingsTitle"] = "设置",
            ["SettingsSubtitle"] = "启动、托盘、刷新、语言和本地统计。",
            ["StartWithWindows"] = "跟随 Windows 启动",
            ["StartMinimized"] = "启动后最小化到托盘",
            ["CloseToTray"] = "关闭按钮最小化到托盘",
            ["AlwaysOnTop"] = "窗口置顶显示",
            ["PersistStats"] = "本地保存统计信息",
            ["HideIdleRows"] = "隐藏低于最小总字节数的行",
            ["UseBitsPerSecond"] = "速率显示为 bit/s",
            ["ExcludeLocalTraffic"] = "排除本机回环流量",
            ["RefreshInterval"] = "刷新间隔（1-10 秒）",
            ["MinimumBytes"] = "最小可见总字节数",
            ["Language"] = "语言",
            ["StartupNote"] = "开机启动使用 Windows 任务计划程序，并以最高权限运行。保存设置时会创建或移除该任务。",
            ["Theme"] = "主题",
            ["ThemeSystem"] = "跟随系统",
            ["ThemeDark"] = "深色",
            ["ThemeLight"] = "浅色",
            ["VisibleColumns"] = "显示列",
            ["ShowPidColumn"] = "PID",
            ["ShowRateColumns"] = "速率列",
            ["ShowTotalColumns"] = "接收/发送总量",
            ["ShowIpSplitColumns"] = "IPv4 / IPv6 分项",
            ["ShowProtocolColumns"] = "TCP / UDP 分项",
            ["ShowFlowsColumn"] = "流",
            ["ShowPathColumn"] = "路径",
            ["ResetStatistics"] = "重置统计数据",
            ["ResetWarning"] = "这会永久清除 FlowLens 本地保存的所有流量统计。确定继续吗？",
            ["ResetDone"] = "统计数据已重置。",
            ["Cancel"] = "取消",
            ["Save"] = "保存",
            ["Show"] = "显示",
            ["Exit"] = "退出"
        }
    };

    public static string NormalizeLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language) && Languages.ContainsKey(language))
        {
            return language;
        }

        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";
    }

    public static string T(string language, string key)
    {
        language = NormalizeLanguage(language);
        if (Languages.TryGetValue(language, out var strings) && strings.TryGetValue(key, out var value))
        {
            return value;
        }

        return Languages["en-US"].TryGetValue(key, out var fallback) ? fallback : key;
    }
}

public enum TrafficTimeRange
{
    Session,
    Today,
    Last7Days,
    Last30Days,
    All
}

public enum AppTheme
{
    Dark = 0,
    Light = 1,
    System = 2
}

public sealed record TimeRangeItem(TrafficTimeRange Range, string Label)
{
    public override string ToString() => Label;
}
