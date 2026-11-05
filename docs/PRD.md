# PRD.md (Product Requirements Document)

## 1. Product Overview
**Name:** My Notch
**Concept:** A high-performance, native Windows desktop overlay inspired by macOS's "Dynamic Island," heavily optimized for developer workflows. 
**Goal:** Provide zero-friction access to local environments, AI coding agents, and system utilities while operating seamlessly in the background with minimal footprint.

## 2. Tech Stack & Architecture
* **Language & Framework:** C# 12 / .NET 8 (or 9) with WinUI 3 (Windows App SDK).
* **Compilation:** Native AOT (Ahead-of-Time) to eliminate runtime overhead and achieve instant startup with a sub-50MB memory footprint.
* **UI/UX:** Borderless, transparent, always-on-top window using Windows 11 Mica/Acrylic materials and fluid geometry animations.
* **Background Server:** ASP.NET Core Minimal API (running locally inside the app) for webhook ingestion.

## 3. Core Features & OS Integrations
1. **Screen Share Evasion (Teleprompter Mode):**
   * Must utilize `SetWindowDisplayAffinity` with `WDA_EXCLUDEFROMCAPTURE` so the notch is invisible to Teams, Zoom, Google Meet, and standard screen recording.
2. **AI Agent Status (Local Webhooks):**
   * Embedded local HTTP listener (`localhost:9999`) to receive `POST` payloads from local AI harnesses (Claude Code, Kenari Studio, etc.).
   * UI indicates state: Running, Awaiting Approval, Done. Clicking brings the respective terminal/IDE to the foreground.
3. **Port & Process Manager:**
   * Natively parse `netstat` or `GetExtendedTcpTable` to map active ports (e.g., 3000, 5432) to processes.
   * Provide a one-click "Kill Process" button for hung ports.
4. **Developer Clipboard:**
   * Subscribe to OS clipboard events (`AddClipboardFormatListener`).
   * Auto-detect developer strings: If clipboard starts with `eyJ` (JWT) or `{` (JSON), surface an instant "Decode/Format" action button in the notch.
5. **System Media Transport Controls (SMTC):**
   * Use `Windows.Media.Control` API to fetch current playing track from Spotify/Apple Music/Browsers without external OAuth.
   * *Stretch:* LRCLIB integration for synced `.lrc` lyrics.
6. **Productivity & Reminders:**
   * Pomodoro/Focus timer with "Context Lock" (suppresses non-dev notifications).
   * Aladhan API integration for coordinate-based prayer times.
7. **Quick Command Palette:**
   * Global hotkey (e.g., `Alt + Space`) expands the notch to a text input field for executing saved PowerShell scripts, opening repos, or SSHing into servers.
