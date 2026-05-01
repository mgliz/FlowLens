# Changelog

## 1.0.1 - 2026-05-01

- Fixed per-PID traffic snapshot keys to avoid cross-process counter mixing for shared executables.
- Excluded local loopback and local-to-local traffic by default to better match external adapter throughput.
- Fixed rate calculation to use the actual sampling interval.
- Ignored unknown process records when loading or persisting history.
- Fixed About window image packaging in single-file builds.

## 1.0.0 - 2026-05-01

- Initial public release.
- Added per-process TCP and UDP traffic monitoring.
- Added IPv4 and IPv6 receive/send split.
- Added persisted local statistics and selectable time ranges.
- Added tray mode, startup settings, configurable columns, and reset confirmation.
- Added dark, light, and follow-system themes.
- Added modern scrollbar styling, about window, and bilingual UI.
