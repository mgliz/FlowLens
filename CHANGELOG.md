# Changelog

## 1.0.5 - 2026-09-19

- Add custom start/end dates and hours, shared by process and physical-adapter statistics.
- Store new samples in hourly buckets; assign both process and adapter deltas to the snapshot's local hour.
- Import the corrected daily histories once into new versioned files, retaining daily granularity without inventing hourly detail. Legacy daily totals are included only when the entire day is selected.
- Fix corrected invalid date input requiring two Apply clicks.
- Publish a self-contained Windows x64 package with .NET included.
- Save the selected dates across restarts and show the applied period while editing.
- Validate missing, reversed, and future dates; provide English and Chinese controls and guidance.
- Add regression coverage for custom ranges, cross-month boundaries, single days, empty history, adapter isolation, and settings round trips.

- Normalize TCP receive addresses and ports to packet direction before adapter filtering; UDP receive events keep their existing packet direction.
- Require a known endpoint on the selected adapter instead of accepting arbitrary receive destinations, preventing cross-adapter TCP receive traffic from entering its totals.
- Use the same connection key for TCP sends and receives.
- Add calendar-month ranges for this month and last month without changing existing saved range values.
- Label binary byte quantities as KiB, MiB, GiB, and TiB.
- Preserve earlier histories and store hourly data in process v9 and adapter v8 histories. Only the corrected local daily v8/v7 histories are eligible for import; older accounting histories remain isolated.
- Add endpoint, connection, and calendar-boundary regression coverage.

- Persist every source snapshot before coalescing UI refreshes, preventing physical-history gaps while the window is busy.
- Retain process persistence baselines until counters actually disappear from the source, preventing repeated replay of idle PID 0 traffic.
- Release the single-instance mutex only when owned, fixing the secondary instance's shutdown crash.
- Track capture continuity across failures, restarts, and unavailable adapters so incomplete intervals cannot enter aligned history.
- Stop snapshot production and drain capture before publishing and saving the final interval on normal exit.
- Preserve unreadable history files and surface the load failure instead of replacing them with empty history.
- Add regression coverage for coalesced rendering, idle counters, persistence toggles, reset generations, capture lifecycle, mutex ownership, and history recovery.

## 1.0.4 - 2026-08-18

- Rebuilt Windows startup registration with an unquoted executable action, a per-user delayed logon trigger, battery-safe settings, no 72-hour execution limit, and automatic repair after the executable moves or the task disappears.
- Synchronized the native Windows title bars with dark, light, and follow-system theme changes across every application window.

## 1.0.3 - 2026-08-18

- Reworked the interface around separate physical-adapter and process-attribution sections so unlike accounting sources are no longer presented as one interchangeable total.
- Centralized dark, light, and control resources; improved light-theme accent contrast, focus states, numeric alignment, search affordances, and responsive toolbar behavior.
- Reorganized settings into task-based sections with validated numeric input, global live theme previews, cancel rollback, and Per-Monitor-V2 DPI behavior.
- Added a frozen process column, highlighted sort headers, a persistent sort summary, stable secondary sorting, a visible search placeholder and clear action, and more compact defaults for new installations.
- Aligned IPv4, IPv6, TCP, and UDP receive/send column sorting with the combined values shown in the table.
- Partitioned process and physical history by the actual network-interface ID and start a new capture epoch when automatic adapter selection changes.
- Paused adapter-aligned persistence whenever the selected adapter or its address set cannot be attributed reliably.
- Moved compact history serialization and durable writes to a coalescing background worker.
- Made ETW shutdown wait for its worker tasks and retry an unexpectedly stopped capture session with backoff.
- Decoupled settings persistence from Task Scheduler registration and surfaced startup-registration failures.
- Coalesced UI dispatch so a busy window keeps only the newest monitor snapshot.
- Matched ETW sends to the selected adapter while retaining receives whose destination was rewritten by WFP/TUN and does not belong to another local interface.
- Started synchronized v5 process and v4 adapter history files so earlier over-counted and over-filtered data remains isolated.
- Kept PID 0 kernel-network events as a visible, persisted system/unattributed row instead of silently dropping their bytes.
- Made summary metrics independent of search and row-visibility filters.
- Separated physical network-interface totals from logical ETW per-process traffic so VPN and transparent-proxy tunnel legs are not presented as adapter usage.
- Added selectable interface sampling, ETW lost-event warnings, larger trace buffers, and explicit adapter-aligned traffic labels.
- Fixed PID reuse and mutable process metadata resetting history baselines.
- Normalized Windows executable paths case-insensitively and safely merged equivalent history identities.
- Fixed transient adapter-read failures inflating the next displayed rate or switching the total back to the ETW logical sum.
- Switched rate timing and maintenance intervals to monotonic clocks.
- Reduced ETW callback allocations, bounded recent-flow tracking, staggered process identity checks, and suppressed flow tracking while hidden.
- Added compact atomic history saves with bounded retry behavior and isolated corrected history from legacy `history.json`.
- Added regression coverage for counter deltas, PID instances, path identity, saturation, time buckets, and adapter sampling.

## 1.0.2 - 2026-05-02

- Changed Windows startup registration to Task Scheduler with highest privileges, replacing the previous Run registry entry.
- Reduced long-running background overhead by suppressing table refreshes while the window is hidden or minimized.
- Pruned stale ETW counters, flow sets, and process cache entries after inactivity.

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
