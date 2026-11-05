# MyNotch

A native Windows desktop notch that brings music, AI agent activity, productivity tools, and system monitoring into a compact panel at the top of the screen.

MyNotch is built with C# 12, .NET 8, and WinUI 3 (Windows App SDK 1.6). It runs as an unpackaged desktop application with a tray icon. Hover over the notch to expand it. Settings include monitor selection, following the active window's monitor, hiding in fullscreen, and starting with Windows. Launching a second instance opens the existing one.

## Current features

| Area | Implemented behavior |
| --- | --- |
| Music | Windows media-session playback controls, artwork, track progress, and automatic LRCLIB lyrics. Timed lyrics can appear in the collapsed notch; plain lyrics remain available without claiming synchronization. |
| Calendar | Month navigation, dated local to-dos, completion tracking, and optional task reminders. A published ICS feed configured in Settings adds upcoming-meeting alerts and join links. |
| Focus | Configurable focus, short-break, and long-break timers with pause, resume, reset, skip, and four-session round progress. Defaults are 25/5/15 minutes. |
| Prayer | Aladhan prayer schedules, next-prayer countdown and reminders, sunrise, imsak, and calculated Qibla bearing. Ten Indonesian location presets; Jakarta is the default. |
| Agents | Session activity and approval alerts through local webhooks; Claude Code permission responses; account usage cards for Codex, Claude Code, Antigravity, and Muse Code. Codex reset credits can be consumed manually when the account offers one. |
| System | CPU, GPU, memory, drives, network, and battery/power views with live readings, history charts, and process or device details where available. |
| Developer | Local project dev-server discovery with open/stop/restart actions, Docker container controls, Git repository and GitHub Actions status, and clipboard tools/history. Clipboard recognition includes JSON, JWT, timestamps, UUIDs, Base64, URLs, colors, and cron expressions. |
| Commands | Searchable saved terminal, SSH, and PowerShell script actions. Unmatched free text is never executed. |
| Notifications | Focus, prayer, task, meeting, wellness, quota, and sustained CPU/RAM alerts, plus Bluetooth, volume, brightness, and optional Windows app notifications. Category controls and recent notifications are available from Settings or the tray/right-click menu. |

## Build and run

Use Windows with the **.NET 8 SDK**, **Visual Studio 2022 or its Build Tools**, and the Windows SDK/Appx MSBuild tooling needed by WinUI. Native AOT publishing also needs the C++ native toolchain. The project targets `net8.0-windows10.0.19041.0` and declares a minimum Windows platform version of `10.0.17763.0`.

Run these commands from the repository root in PowerShell:

```powershell
.\build.ps1
& .\MyNotch\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MyNotch.exe
```

The build script uses `dotnet` from PATH, falling back to `.\.dotnet\dotnet.exe`. It keeps CLI state and NuGet packages in `.dotnet-home` and `.nuget` and uses `vswhere` to locate Visual Studio's Appx tasks. If no SDK is installed, the included installer can provision a local one:

```powershell
.\dotnet-install.ps1 -Channel 8.0 -InstallDir .\.dotnet
```

Other build modes:

```powershell
.\build.ps1 -Configuration Release
.\build.ps1 -PublishAot
```

The script builds/publishes **x64**. The project also declares ARM64, but the script does not select it. Keep the generated resources and dependencies alongside the executable; this is not a single-file deployment.

**Known publishing limitation:** the latest recorded Native AOT verification compiled successfully but the published executable failed during WinUI startup with `0xc0000374`. Use a regular Debug/Release build for now. See [notification and publishing notes](docs/NOTIFICATIONS.md) for the recorded investigation. These notes are not a guarantee that every machine has been tested.

## Configuration and integrations

Open **Settings** in the notch or from its tray menu. User data lives in `%LOCALAPPDATA%\MyNotch`:

| File or directory | Purpose |
| --- | --- |
| `settings.json` | General, display, focus, wellness, agent, developer, and calendar-feed preferences. |
| `notification-settings.json` | Notification category preferences. |
| `calendar-tasks.json` | Dated tasks and reminder delivery state. |
| `prayer-location.txt` | Selected prayer location. |
| `commands.json` | Optional saved command definitions. |
| `usage-resets/` | Idempotency journal for account reset-credit requests. |

Clipboard history, recent notification contents, and account usage snapshots are kept in memory rather than persisted. Lyrics lookup sends track metadata to LRCLIB; prayer schedules use Aladhan; a configured ICS feed is fetched from its URL. Account usage integrations use the installed provider CLI or the existing Claude Code credentials. Availability depends on those providers and their signed-in sessions.

### Saved commands

Copy [commands.example.json](MyNotch/commands.example.json) to `%LOCALAPPDATA%\MyNotch\commands.json`, replace its sample targets, and restart MyNotch. A `commands.json` beside the executable is used if no per-user file exists. Without a valid command list, the palette provides a home-directory Windows Terminal action. Terminal actions require `wt.exe`; SSH and script actions require their corresponding executables and targets.

### Agent activity

MyNotch listens on `http://127.0.0.1:9999`:

- `GET /health`: listener health check.
- `POST /agent`: generic agent activity (`running`, `awaiting_approval`, `done`, or `error`).
- `POST /claude/hook`: Claude Code session updates.
- `POST /claude/permission`: waits for a notch approval decision or falls back to the terminal prompt.

With MyNotch running, send an example activity update:

```powershell
$payload = @{
    agent = 'my-agent'
    status = 'done'
    message = 'Build finished'
    session = 'demo'
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:9999/agent' -ContentType 'application/json' -Body $payload
```

For Claude Code, use **Settings > Copy Claude Code hooks**, merge the copied hooks into your Claude settings, and restart Claude Code. See [Claude Code integration](docs/CLAUDE-CODE-HOOKS.md). Requests carrying a browser `Origin` header are rejected. If the listener cannot start, the app surfaces the error; port conflicts or HTTP URL reservations can prevent it from listening.

Usage cards require their corresponding installed and authenticated tools. Docker features require Docker; repository status requires Git, and GitHub Actions status requires an authenticated `gh` CLI. Missing integrations show unavailable states rather than fabricated data.

### Windows app notifications

The default unpackaged executable cannot read Windows app notifications without package identity and OS consent. Use **Notification settings > Set up Windows access**, then follow the restart/Start-menu and consent steps in [notification setup](docs/NOTIFICATIONS.md). The bundled helper registers a local development identity pointing at the build folder; Windows development/sideloading policy must allow registration. Other notification categories work independently of this setup.

## Verification

The regression suite is a console executable, despite its historical `Lyrics.Tests` name. It covers lyrics and artwork, timers, prayer calculations, calendar tasks, usage parsing/lifecycles, notifications, settings, clipboard/ICS parsing, webhooks, and developer-tool behavior.

```powershell
dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj
```

If using the local SDK, replace `dotnet` with `.\.dotnet\dotnet.exe`. These test projects are not included in `MyNotch.sln`; run them explicitly. Windows runs also exercise the local HTTP integration.

Optional live diagnostics:

```powershell
# Read account usage through installed provider integrations
dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj -- --usage-probe

# Sample the Windows system dashboard
dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj -- --system-dashboard-probe

# Inspect active Windows media sessions
dotnet run --project tests/MyNotch.MediaProbe/MyNotch.MediaProbe.csproj
```

The dev-server probe is documented in [dev-server notes](docs/DEV-SERVERS.md); it starts/stops its own temporary HTTP servers and currently expects Python at `C:\Python312\python.exe`. Live probes depend on local software, accounts, or hardware and are separate from ordinary regression checks. UI layout and hardware event delivery still need manual verification.

## Code layout and further reading

- `MyNotch/MainWindow*.cs` and `MainWindow.xaml`: window behavior, page composition, and feature wiring.
- `MyNotch/Controls`, `ViewModels`, and `Themes`: native UI controls, presentation state, and styling.
- `MyNotch/Models`: data types, parsers, and state-transition logic.
- `MyNotch/Services` and `Interop`: persistence, external integrations, timers, and Windows APIs.
- `tests/`: console regression suite and live media probe.
- `docs/`: [music](docs/MUSIC.md), [focus](docs/FOCUS.md), [prayer](docs/PRAYER.md), [calendar/system](docs/CALENDAR-SYSTEM.md), [agent usage](docs/AGENT-USAGE.md), [notifications](docs/NOTIFICATIONS.md), and [dev servers](docs/DEV-SERVERS.md).

[PRD](docs/PRD.md), [plan](docs/PLAN.md), and [design](docs/DESIGN.md) capture project intent and development history; some sections describe earlier behavior or planned work. Current limits include no prayer audio, no FPS capture, no authenticated Apple lyrics provider, and no source-project attribution for WSL/Docker proxy ports in the dev-server list. Calendar feeds are read-only, not a calendar-account synchronization service.

## License

[Apache License 2.0](LICENSE).
