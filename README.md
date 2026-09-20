# FlowLens

> This project was developed with the assistance of AI tools.

FlowLens is a lightweight Windows traffic monitor that aggregates TCP and UDP traffic by process, with separate IPv4 and IPv6 statistics.

![FlowLens icon](Assets/FlowLens.png)

## Screenshot

![FlowLens main window](docs/screenshot.png)

## Features

- Per-process TCP and UDP traffic statistics.
- IPv4 and IPv6 receive/send split.
- Real-time rate view plus persisted local statistics.
- Optional time ranges: current session, today, this month, last month, last 7 days, last 30 days, all, and custom dates/hours.
- Configurable columns, minimum visible traffic threshold, and refresh interval.
- Tray mode, close-to-tray, start with Windows, and start minimized.
- Dark, light, and follow-system themes.
- Optional always-on-top window and bit/s rate display.
- English and Simplified Chinese UI.

## Requirements

- Windows 10/11 x64.
- The official self-contained Windows x64 download includes the .NET runtime; no separate .NET installation is required.
- Administrator privileges for ETW network capture.

## Download

The development branch builds **1.0.6-preview.3 (test build)**, not an official Release. Local builds show a test badge and the full prerelease version in About. The published stable download below remains 1.0.5.

Download the self-contained [FlowLens 1.0.5 Windows x64 package](https://github.com/mgliz/FlowLens/releases/download/v1.0.5/FlowLens-1.0.5-win-x64.zip) from [GitHub Releases](https://github.com/mgliz/FlowLens/releases/latest):

```text
FlowLens-1.0.5-win-x64.zip
```

Unzip it and run `FlowLens.exe` as administrator.

## Build

```powershell
dotnet restore
dotnet build .\FlowLens.csproj -c Release
dotnet publish .\FlowLens.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Data

FlowLens stores settings and local traffic history under:

```text
%APPDATA%\FlowLens
```

## Accounting model

- The main total uses byte counters from the selected Windows network interface.
- Process rows use kernel ETW events. TCP receive endpoints are normalized separately from UDP. Sends must originate on the selected adapter and receives must target it; events with an unknown local endpoint are excluded from adapter attribution.
- A VPN, TUN adapter, or transparent proxy can expose both an application's original connection and the proxy's outer connection. Address filtering narrows attribution to the selected adapter, but rewritten receive endpoints cannot always be proven to belong to it. Process IPv4 totals are not a campus-billing meter.
- IPv4 and IPv6 totals each include received and sent bytes. Byte quantities use binary units (KiB, MiB, GiB); 1 GiB is 1,073,741,824 bytes. Compare the same dates, directions, units, and network when checking another meter.
- If Windows reports lost ETW events, FlowLens displays a capture warning because affected process totals may be incomplete.

## Notes

### Updates

Open **About → Check for updates** to query `mgliz/FlowLens` on GitHub. Startup checks run at most once per day and can be disabled in Settings. Only stable Releases are offered. When the anonymous API is rate-limited, FlowLens uses the repository's official latest-Release page and checksum asset; no GitHub token is required.

Choose **Download and update** for a newer stable version, or **Switch to official release** from a test build. Switching replaces unreleased test features even if the official version number is lower. After confirmation, the app downloads and checks SHA-256 and the EXE version, saves history, and restarts at the same installed path. A backup EXE is kept beside the installation. Cancellation or preflight failure leaves the app running. Installation failures are logged under `%APPDATA%\FlowLens\updates`; failed startup restores the backup for a manual restart. Settings, history, and startup paths are preserved. The installation folder must be writable.

更新入口：**关于 → 检查更新**。启动时每天自动检查一次，可在设置中关闭。仅读取本项目正式 GitHub Release，不需要 Token。测试版会显示“切换到正式版”，切换后未发布的测试功能会被正式版替代。校验成功后保存历史、原位更新并重启，保留旧 EXE 备份。

### Custom period

The applied period appears as one compact summary. Click **Edit** to open a floating editor without moving the statistics or table; **Cancel**, Escape, or clicking outside restores the applied values. Calendars use the app's light/dark theme, including month/year navigation and selected dates. Future dates are visible but disabled. The summary ends at the last minute of the selected hour (for example 10:59), while the underlying interval still includes every second before 11:00. The short legacy-history note has a tooltip with the full limitation.

To choose a period, select **Custom period**, pick the start and end dates/hours, and click **Apply**. Both selected hours are included: 09:00 through 10:00 means [09:00, 11:00), displayed as 09:00–10:59. Selection survives restarts and applies to process totals (including IPv4/IPv6) and physical-adapter totals; rates remain live. New history uses local calendar-hour buckets. Each sampling interval is assigned to the snapshot hour, so traffic around an hour boundary can shift by one sampling interval (normally 1 second, configurable to 10); it is not per-packet timestamp reconstruction. Repeated local hours during daylight-saving changes share a bucket.

自选时段：选择 **自定义时段**，设置起止日期和小时后点击 **应用**。包含首尾所选小时，例如 09:00 至 10:00 表示统计 09:00 到 11:00 之前的流量。进程和网卡总量使用同一范围，重启后保留选择，速率仍为实时值。新数据按本地小时保存；跨整点的一次采样归到采样结束所在小时，边界可能偏移一个采样间隔。旧日统计不会拆分或平均分配到小时，仅当范围完整包含该天时计入；选择部分小时会排除该日旧数据，界面会持续显示这一限制。

FlowLens counts traffic while it is running. It does not backfill traffic that happened before the app started.

Hourly process accounting is stored in `history-v9.json`, with adapter totals in `network-history-v8.json`. Buckets are isolated by the actual Windows interface ID. On the first start with no new history file, the corrected local daily files (`history-v8.json` / `network-history-v7.json`) are copied in memory with their original daily keys and subsequently saved to the new files. Originals are preserved. Restarting uses the new files and does not import again. Older, known-incompatible accounting histories remain isolated. A corrupt new file blocks saving and is not replaced by fallback data. Daily totals are included only for fully selected calendar days; missing hourly detail cannot be recovered.

Every source snapshot updates history before UI rendering can coalesce refreshes. Incomplete intervals spanning capture failures or adapter recovery establish a new baseline and are excluded from both histories. A normal exit records the final complete interval before saving. If a history file cannot be read, the app reports a persistence error and refuses to overwrite that file.

Linux is not supported by this WPF/ETW version. A Linux build would require a separate UI and capture backend.

## License

MIT License.
