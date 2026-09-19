using System.Globalization;

namespace FlowLens;

public static class Localizer
{
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = new()
    {
        ["en-US"] = new Dictionary<string, string>
        {
            ["AppSubtitle"] = "Per-process TCP / UDP traffic on the selected adapter, split by IPv4 / IPv6",
            ["SearchPlaceholder"] = "Search process, PID, or path",
            ["ClearSearch"] = "Clear search",
            ["TimeRange"] = "Statistics time range",
            ["PhysicalNetworkTraffic"] = "Physical network traffic",
            ["AttributedProcessTraffic"] = "Attributed process traffic",
            ["ProcessDetails"] = "Process traffic details",
            ["SortBy"] = "Sort",
            ["Ascending"] = "Ascending order",
            ["Descending"] = "Descending order",
            ["CurrentRate"] = "Current rate",
            ["ReceiveRate"] = "Receive rate",
            ["SendRate"] = "Send rate",
            ["PeriodTotal"] = "Selected range total",
            ["AdapterUnavailable"] = "Adapter unavailable",
            ["Settings"] = "Settings",
            ["About"] = "About",
            ["AboutTitle"] = "About FlowLens",
            ["AboutSubtitle"] = "Lightweight Windows per-process traffic monitor.",
            ["AboutVersion"] = "Version",
            ["AboutFeatures"] = "Physical adapter total plus adapter-aligned ETW per-process IPv4 / IPv6 and TCP / UDP statistics.",
            ["AboutRuntime"] = ".NET 8 Windows desktop app. Administrator privileges are required. Process events are matched to the selected adapter; proxied traffic is normally attributed to the proxy process.",
            ["LegacyHistoryPreserved"] = "Earlier accounting files remain on disk and are not mixed into the synchronized corrected history.",
            ["AboutData"] = "Local data",
            ["AboutLicense"] = "License",
            ["AboutLicenseValue"] = "MIT License",
            ["AboutAi"] = "This project was completed with AI assistance.",
            ["Close"] = "Close",
            ["Reset"] = "Reset",
            ["TotalCurrent"] = "Total / current",
            ["LogicalTotalCurrent"] = "Attributed total / current",
            ["NetworkTotalCurrent"] = "Network total / current",
            ["Ipv4Logical"] = "IPv4 attributed",
            ["Ipv6Logical"] = "IPv6 attributed",
            ["ProcessesFlows"] = "Processes / flows",
            ["Process"] = "Process",
            ["Unattributed"] = "System / unattributed",
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
            ["StorageWarning"] = "History save warning",
            ["AdapterUnavailableWarning"] = "Network adapter counters are temporarily unavailable",
            ["LostEvents"] = "lost ETW events:",
            ["ResetComplete"] = "Reset complete; waiting for traffic",
            ["TrayHint"] = "Physical totals above · ETW attribution below",
            ["RangeSession"] = "Session",
            ["RangeToday"] = "Today",
            ["Range7Days"] = "Last 7 days",
            ["Range30Days"] = "Last 30 days",
            ["RangeAll"] = "All",
            ["RangeThisMonth"] = "This month",
            ["RangeLastMonth"] = "Last month",
            ["SettingsTitle"] = "Settings",
            ["SettingsSubtitle"] = "Startup, tray behavior, refresh, language, and local statistics.",
            ["GeneralSettings"] = "General",
            ["CaptureSettings"] = "Capture",
            ["AppearanceSettings"] = "Appearance",
            ["DataSettings"] = "Local data",
            ["StartWithWindows"] = "Start with Windows",
            ["StartMinimized"] = "Start minimized to tray",
            ["CloseToTray"] = "Close button minimizes to tray",
            ["AlwaysOnTop"] = "Keep window always on top",
            ["PersistStats"] = "Save statistics locally",
            ["HideIdleRows"] = "Hide rows below minimum total bytes",
            ["UseBitsPerSecond"] = "Show rates as bit/s",
            ["ExcludeLocalTraffic"] = "Exclude loopback and local-to-local traffic",
            ["NetworkInterface"] = "Monitored network adapter",
            ["AdapterAutomatic"] = "Automatic (physical adapter)",
            ["RefreshInterval"] = "Refresh interval (1-10 seconds)",
            ["MinimumBytes"] = "Minimum visible total bytes",
            ["InvalidRefreshInterval"] = "Choose a refresh interval from 1 to 10 seconds.",
            ["InvalidMinimumBytes"] = "Minimum visible bytes must be a non-negative whole number.",
            ["Language"] = "Language",
            ["StartupNote"] = "Startup uses Windows Task Scheduler with highest privileges. It starts shortly after sign-in, including on battery power, and is repaired if the app moves.",
            ["StartupRegistrationFailed"] = "Windows startup could not be updated:",
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
            ["AppSubtitle"] = "按所选网卡统计进程 TCP / UDP 流量，并区分 IPv4 / IPv6",
            ["SearchPlaceholder"] = "搜索进程、PID 或路径",
            ["ClearSearch"] = "清除搜索",
            ["TimeRange"] = "统计时间范围",
            ["PhysicalNetworkTraffic"] = "物理网卡流量",
            ["AttributedProcessTraffic"] = "进程归因流量",
            ["ProcessDetails"] = "进程流量明细",
            ["SortBy"] = "排序",
            ["Ascending"] = "升序",
            ["Descending"] = "降序",
            ["CurrentRate"] = "当前速率",
            ["ReceiveRate"] = "接收速率",
            ["SendRate"] = "发送速率",
            ["PeriodTotal"] = "所选时段总量",
            ["AdapterUnavailable"] = "网卡不可用",
            ["Settings"] = "设置",
            ["About"] = "关于",
            ["AboutTitle"] = "关于 FlowLens",
            ["AboutSubtitle"] = "轻量级 Windows 按进程流量监控工具。",
            ["AboutVersion"] = "版本",
            ["AboutFeatures"] = "网卡实测总量，以及与所选网卡对齐的 ETW 进程 IPv4 / IPv6、TCP / UDP 分项统计。",
            ["AboutRuntime"] = ".NET 8 Windows 桌面应用，需要管理员权限。进程事件按所选网卡匹配；代理流量通常归属代理进程。",
            ["LegacyHistoryPreserved"] = "较早的统计文件仍保留在磁盘中，不会混入起点同步后的修正统计。",
            ["AboutData"] = "本地数据",
            ["AboutLicense"] = "开源协议",
            ["AboutLicenseValue"] = "MIT License",
            ["AboutAi"] = "本项目由 AI 辅助完成。",
            ["Close"] = "关闭",
            ["Reset"] = "重置",
            ["TotalCurrent"] = "总量 / 当前",
            ["LogicalTotalCurrent"] = "归因总量 / 当前",
            ["NetworkTotalCurrent"] = "网卡总量 / 当前",
            ["Ipv4Logical"] = "IPv4 归因流量",
            ["Ipv6Logical"] = "IPv6 归因流量",
            ["ProcessesFlows"] = "进程 / 流",
            ["Process"] = "进程",
            ["Unattributed"] = "系统 / 未归属",
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
            ["StorageWarning"] = "历史保存警告",
            ["AdapterUnavailableWarning"] = "网卡计数器暂时不可用",
            ["LostEvents"] = "ETW 丢失事件：",
            ["ResetComplete"] = "已重置，等待新流量",
            ["TrayHint"] = "上方：网卡实测 · 下方：ETW 进程归因",
            ["RangeSession"] = "本次运行",
            ["RangeToday"] = "今天",
            ["Range7Days"] = "最近 7 天",
            ["Range30Days"] = "最近 30 天",
            ["RangeAll"] = "全部",
            ["RangeThisMonth"] = "本月",
            ["RangeLastMonth"] = "上月",
            ["SettingsTitle"] = "设置",
            ["SettingsSubtitle"] = "启动、托盘、刷新、语言和本地统计。",
            ["GeneralSettings"] = "常规",
            ["CaptureSettings"] = "采集",
            ["AppearanceSettings"] = "外观",
            ["DataSettings"] = "本地数据",
            ["StartWithWindows"] = "跟随 Windows 启动",
            ["StartMinimized"] = "启动后最小化到托盘",
            ["CloseToTray"] = "关闭按钮最小化到托盘",
            ["AlwaysOnTop"] = "窗口置顶显示",
            ["PersistStats"] = "本地保存统计信息",
            ["HideIdleRows"] = "隐藏低于最小总字节数的行",
            ["UseBitsPerSecond"] = "速率显示为 bit/s",
            ["ExcludeLocalTraffic"] = "排除回环及本机内部流量",
            ["NetworkInterface"] = "监控网卡",
            ["AdapterAutomatic"] = "自动（物理联网网卡）",
            ["RefreshInterval"] = "刷新间隔（1-10 秒）",
            ["MinimumBytes"] = "最小可见总字节数",
            ["InvalidRefreshInterval"] = "请选择 1 到 10 秒的刷新间隔。",
            ["InvalidMinimumBytes"] = "最小可见字节数必须是非负整数。",
            ["Language"] = "语言",
            ["StartupNote"] = "开机启动使用 Windows 任务计划程序并以最高权限运行。登录后稍候启动，使用电池时也可运行；移动程序后会自动修复任务。",
            ["StartupRegistrationFailed"] = "无法更新 Windows 开机启动：",
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
    Session = 0,
    Today = 1,
    Last7Days = 2,
    Last30Days = 3,
    All = 4,
    ThisMonth = 5,
    LastMonth = 6
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
