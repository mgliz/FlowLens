# FlowLens

**English** | [简体中文](README.zh-CN.md)

> This project was developed with the assistance of AI tools.

FlowLens is a lightweight Windows traffic monitor. It shows physical traffic for the selected network adapter and attributes TCP and UDP traffic to processes, with separate IPv4 and IPv6 statistics.

![FlowLens icon](Assets/FlowLens.png)

## Screenshot

![FlowLens main window](docs/screenshot.png)

## Features

- Physical network-adapter totals and logical per-process traffic are presented separately.
- Per-process TCP and UDP statistics with IPv4/IPv6 and receive/send breakdowns.
- Real-time rates plus persistent local history.
- Time ranges for the current session, today, this month, last month, the last 7 or 30 days, all history, and custom dates and hours.
- A compact, responsive interface with themed custom-period controls, calendars, tooltips, tables, and update status.
- Configurable columns, minimum visible traffic threshold, and refresh interval.
- Tray mode, close to tray, start with Windows, and start minimized.
- Dark, light, and follow-system themes, an always-on-top option, and optional bit/s rate display.
- English and Simplified Chinese UI.
- Stable-release checks and verified in-place updates from GitHub.

## Requirements

- Windows 10/11 x64.
- Administrator privileges for ETW network capture.
- The official self-contained Windows x64 package includes the .NET runtime; no separate .NET installation is required.

## Download

Download the self-contained [FlowLens 1.0.6 Windows x64 package](https://github.com/mgliz/FlowLens/releases/download/v1.0.6/FlowLens-1.0.6-win-x64.zip) from [GitHub Releases](https://github.com/mgliz/FlowLens/releases/latest):

```text
FlowLens-1.0.6-win-x64.zip
```

Unzip the package and run `FlowLens.exe` as administrator.

Read the [FlowLens 1.0.6 release notes](docs/releases/v1.0.6.md) or the [complete changelog](CHANGELOG.md).

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

- The physical total comes from byte counters reported by the selected Windows network interface.
- Process rows use kernel ETW events. TCP receive endpoints are normalized separately from UDP. Sends must originate on the selected adapter, and receives must target it; events with an unknown local endpoint are excluded from adapter attribution.
- VPNs, TUN adapters, and transparent proxies can expose both an application's original connection and a proxy or tunnel connection. Address filtering narrows attribution to the selected adapter, but ETW cannot always recover the original application behind rewritten traffic. Per-process totals are not a billing meter.
- IPv4 and IPv6 totals each include received and sent bytes. Byte quantities use binary units (KiB, MiB, GiB); 1 GiB is 1,073,741,824 bytes. Compare the same dates, directions, units, and network when checking another meter.
- If Windows reports lost ETW events, FlowLens displays a capture warning because affected process totals may be incomplete.

## Updates

Open **About → Check for updates** to check the official `mgliz/FlowLens` GitHub Releases. FlowLens can also check at startup, at most once per day; this can be disabled in Settings. Only stable releases are offered. If the anonymous GitHub API is rate-limited, FlowLens uses the repository's official latest-release page and checksum asset. No GitHub token is required.

For a newer stable release, choose **Download and update**. FlowLens downloads the package, verifies its SHA-256 checksum and executable version, saves history, replaces the executable in place, and restarts from the same path. It keeps a backup executable beside the installation. Cancellation or a preflight failure leaves the running installation unchanged. Installation failures are logged under `%APPDATA%\FlowLens\updates`; if startup of the replacement fails, the updater restores the backup for a manual restart. Settings, history, and startup paths are preserved. The installation folder must be writable.

Test builds additionally offer **Switch to official release**. This can install the latest stable release even when its version number is lower, replacing unreleased test features.

## Custom periods and history limits

Select **Custom period**, choose the start and end dates and hours, and click **Apply**. Both selected hours are included: 09:00 through 10:00 represents `[09:00, 11:00)` and is displayed as 09:00–10:59. The applied range appears as a compact summary. **Edit** opens a themed floating editor; **Cancel**, Escape, or clicking outside restores the applied values. Future dates are visible but disabled. The selection survives restarts and applies to both process totals and physical-adapter totals, while rates remain live.

New history is stored in local calendar-hour buckets. Each sampling interval is assigned to the hour of its ending snapshot, so traffic at an hour boundary can shift by one sampling interval—normally 1 second and configurable up to 10 seconds. This is not per-packet timestamp reconstruction. Repeated local hours during daylight-saving changes share one bucket.

Hourly process history is stored in `history-v9.json`, with adapter history in `network-history-v8.json`; buckets are isolated by the actual Windows interface ID. On the first start without the new files, corrected daily histories from `history-v8.json` and `network-history-v7.json` are imported once, retaining their daily keys; the originals remain unchanged. Those imported daily totals are included only when a selected range contains the complete calendar day. A range containing only part of that day omits its imported total because hourly detail cannot be reconstructed. Older, incompatible accounting histories remain isolated.

FlowLens counts traffic only while it is running and cannot backfill earlier activity. Every complete source snapshot updates history even when UI refreshes are coalesced. Intervals spanning capture failure or adapter recovery establish a new baseline and are excluded. A normal exit records the final complete interval before saving. An unreadable new history file blocks saving and is preserved rather than overwritten with fallback data.

Linux is not supported by this WPF/ETW version. A Linux build would require a separate UI and capture backend.

## License

FlowLens is available under the [MIT License](LICENSE).
