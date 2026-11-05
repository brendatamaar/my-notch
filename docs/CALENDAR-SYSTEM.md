# Calendar and System Usage

Native tabs with line icons and no page scroll area. Navigation: Music, Calendar, Focus, Prayer, Agents, System Usage, Developer, Commands. Width is 600 DIPs; Calendar height is 280, System Usage 324. Existing tabs retain independent heights.

Calendar is a local Monday-first month grid with six weeks, previous/next, Today, date selection and ISO week/day-of-year. It refreshes today on opening and on the header minute tick while visible. External calendar accounts/events are not connected. Navigation is bounded to years 1-9999.

System Usage shows CPU, physical RAM, system-drive storage, aggregate download/upload speed and uptime. CPU uses GetSystemTimes deltas (kernel includes idle); RAM uses GlobalMemoryStatusEx; uptime uses GetTickCount64. Storage refreshes on opening or Refresh. Network rates use per-adapter byte deltas and Stopwatch elapsed time, excluding down, tunnel and loopback interfaces. New/reset counters cannot produce lifetime-total spikes or negative speeds. Virtual interfaces can contribute to the aggregate. On systems above 64 processors, GetSystemTimes covers the calling processor group.

The one-second UI timer runs only while System Usage is expanded. Collapse, tab change and shutdown stop it and discard baselines. First-sample CPU/network values show -- until a valid delta exists. Native imports use LibraryImport, with no extra packages, performance-counter framework or WMI dependency.

Validation: the dependency-free harness checks month coverage, leap years, Monday alignment, year boundaries, CPU idle accounting, missing/reset samples, adapter changes and byte formatting. Manual QA: month navigation/Today/date selection; compare live CPU/RAM with Task Manager over several samples; verify fresh baselines after collapse/reopen.

Sources:
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
- https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex

## Calendar to-do list

Select a date, enter the task title and click +. Tasks are dated and persist in `%LOCALAPPDATA%/MyNotch/calendar-tasks.json`. Complete/uncomplete with the checkbox; delete with the row button. The day cell gains a blue dot for pending tasks or a muted dot when all are complete. Two tasks per page, with Previous/Next controls, keep the panel free of scroll areas. Titles are trimmed and limited to 200 characters. No calendar account or task content is sent online.

Transitions are pure in CalendarTodo. The store uses versioned source-generated JSON and same-directory temporary-write/rename before updating in-memory state. Corrupt/unknown-version data is preserved and editing disabled; save failures leave existing state intact and show an error. Tests cover add/date/trim, invalid input, completion/deletion, 3125 event sequences, reload persistence and corrupt/write-failed stores.

## System dashboard redesign

The System page now follows Oyenza's module strip and three-column dashboard layout (600 x 324 DIPs): CPU, GPU, memory, drives, network, battery/power. Each module has a live strip value and sparkline; selecting it shows a ring/readout, detail rows, a rolling minute chart and a contextual list. CPU separates user/system and lists top processes; memory includes physical used/available/total and commit; GPU uses Windows GPU Engine PDH counters (busiest engine, aggregated across its processes) with GPU process loads; drives show all ready fixed/removable drives; network lists active adapters, addresses and link capacity; power shows actual battery/AC status. Unsupported power draw/sensor readings stay unavailable; no fabricated watts, temperature or FPS numbers are shown. Game/FPS capture is not implemented.

Sampling is off the UI thread, once per second only while the page is open. Process snapshots refresh every five seconds and storage every thirty. Closing/switching the page fences out late results. History clears on reopening, so the minute chart does not pretend that hidden time was sampled. Paging is used for long drive/adapter lists. Native GPU handles are released on shutdown. Protected processes are skipped safely.

Live dashboard probe: `dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj -- --system-dashboard-probe`.
Reference: https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni/src/pages/system.rs
