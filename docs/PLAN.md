# PLAN.md (Implementation Plan)

## Phase 1: Scaffolding & Windowing (The Invisible Shell)
- [x] Initialize a standard WinUI 3 / Windows App SDK project.
- [x] Configure `.csproj` for Native AOT (`<PublishAot>true</PublishAot>`).
- [x] Strip default window chrome. Make the window transparent, frameless, and always-on-top.
- [x] Implement `SetWindowDisplayAffinity` to exclude the window from screen capture.
- [x] Implement basic expand/collapse animation logic (the "notch" breathing effect).

## Phase 2: AI Webhook Server & UI Binding
- [x] Embed a lightweight `HttpListener` or ASP.NET Core Minimal API to listen on a local port.
- [x] Define JSON schemas for Agent payloads (e.g., `{"agent": "claude-code", "status": "awaiting_approval", "message": "Execute script?"}`).
- [x] Build the UI component to display these alerts and wire up the `DispatcherQueue` to update the UI thread securely.

## Phase 3: Developer Hooks (Clipboard & Ports)
- [x] Implement `AddClipboardFormatListener` to monitor clipboard text.
- [x] Write regex parsers for JWTs and JSON, and build the UI flyout for formatting/decoding.
- [x] Implement port mapping logic using `GetExtendedTcpTable` to list listening ports and bind a "Kill PID" command.

## Phase 4: Media & Productivity Core
- [x] Implement `GlobalSystemMediaTransportControlsSessionManager` to read active media and control playback.
- [x] Build the Pomodoro timer and Aladhan API integration for scheduled reminders.
- [x] Register a global keyboard hook (e.g., `Alt + Space`) to trigger the notch expansion.

## Phase 5: The Command Palette & Polish
- [x] Build the text input palette for quick commands.
- [x] Wire up command execution (launching `wt.exe`, ssh, or `.ps1` scripts).
- [x] Profile memory usage. Ensure idle CPU is 0% and memory footprint is minimal.
- [x] Final AOT compilation testing and binary deployment.

## Phase 6: Oyenza-style redesign
- [x] Classic black island flush with the top edge, Oyenza tokens (accent #F0892E), DPI-aware sizing.
- [x] Closed "glance" states: clock, music (art + lyric line + beat), focus countdown, agent status, clipboard hit, notices, next prayer.
- [x] Open state with tabs: Music, Focus, Agents, Dev (clipboard + ports), Run (command palette). Opens on the relevant tab.
- [x] Hover to open / leave to close; click, Alt+Space or tray pins it open; Esc or click-away closes.
- [x] Tray icon + right-click menu (Open, Command palette, Quit).
- [x] Synced lyrics via LRCLIB (PRD stretch goal).
- [ ] Native AOT publish launches (builds and publishes, but the process shows no window; JIT Release publish works). Needs investigation.

## Phase 7: Settings and integrations
- [x] Settings tab persisted to `%LOCALAPPDATA%\MyNotch\settings.json` (focus lengths, AI services, display, fullscreen, approvals, clips, CI, calendar feed).
- [x] Start with Windows (HKCU Run key, tray toggle; detects a stale path and Task Manager's "disabled").
- [x] Single instance: a second launch opens the running notch.
- [x] Hide while an app is fullscreen (appbar ABN_FULLSCREENAPP + foreground WinEvent hook, no polling); display choice: primary, follow the active window, or a specific monitor.
- [x] Claude Code hooks: per-session tracking, sender window lookup, permission prompts answered from the notch ([CLAUDE-CODE-HOOKS.md](CLAUDE-CODE-HOOKS.md)).
- [x] Clipboard detectors: JWT expiry, Unix timestamps, UUID (v1/v7 time), colors, cron (explained + next runs), URL query/percent-encoding, Base64 text; memory-only history of 10 clips that skips password-manager content.
- [x] Dev servers: Open in browser, Restart (reruns the exact command line in a new console); Docker containers with open/stop; repositories with branch, sync, changes and latest GitHub Actions run, plus a notice when a watched run finishes.
- [x] Next meeting from a published ICS feed: closed-notch countdown, Join link (Meet/Zoom/Teams/Webex), reminder notice.
