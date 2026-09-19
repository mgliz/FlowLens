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
- Optional time ranges: current session, today, this month, last month, last 7 days, last 30 days, all, and custom dates.
- Configurable columns, minimum visible traffic threshold, and refresh interval.
- Tray mode, close-to-tray, start with Windows, and start minimized.
- Dark, light, and follow-system themes.
- Optional always-on-top window and bit/s rate display.
- English and Simplified Chinese UI.

## Requirements

- Windows 10/11 x64.
- .NET 8 Windows Desktop Runtime, unless you publish a self-contained build.
- Administrator privileges for ETW network capture.

## Download

The local `1.0.6` maintenance build is packaged as:

```text
FlowLens-1.0.6-win-x64.zip
```

Unzip it and run `FlowLens.exe` as administrator.

## Build

```powershell
dotnet restore
dotnet build .\FlowLens.csproj -c Release
dotnet publish .\FlowLens.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
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

To choose a period, select **Custom dates**, pick the start and end dates, and click **Apply**. Both dates are included, using local calendar days. The displayed applied range stays visible while editing; edits take effect only after Apply. The selection is saved across restarts and applies to both process totals (including IPv4/IPv6) and physical-adapter totals. Rates remain live. History is stored daily, so hour/minute selection is not supported. Missing days have no recorded traffic; the app cannot recover traffic it did not capture.

自选时段：在统计范围中选择 **自定义日期**，设置开始和结束日期后点击 **应用**。包含首尾两天，按本地日期统计；进程与网卡总量使用同一范围，速率仍为实时值。日期选择会保存，重启后仍有效。历史按天保存，不支持从旧记录查询小时或分钟，也不会补算未采集的流量。

FlowLens counts traffic while it is running. It does not backfill traffic that happened before the app started.

Adapter-aligned process accounting is stored in `history-v8.json`, with matching adapter totals in `network-history-v7.json`. Buckets are isolated by the actual Windows interface ID; earlier history files remain local but are not mixed into corrected totals. Version 1.0.6 starts a new history for the corrected receive-endpoint filter; it does not migrate or delete existing history files.

Every source snapshot updates history before UI rendering can coalesce refreshes. Incomplete intervals spanning capture failures or adapter recovery establish a new baseline and are excluded from both histories. A normal exit records the final complete interval before saving. If a history file cannot be read, the app reports a persistence error and refuses to overwrite that file.

Linux is not supported by this WPF/ETW version. A Linux build would require a separate UI and capture backend.

## License

MIT License.
