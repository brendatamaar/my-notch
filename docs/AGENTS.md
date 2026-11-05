# AGENTS.md (AI Agent Instructions)

## 1. Persona & Role
You are an expert Windows Systems and Desktop Application Developer specializing in C#, WinUI 3, Windows API (Win32 P/Invoke), and highly optimized Native AOT applications. Your goal is to build a hyper-performant, fluid desktop widget that interacts deeply with the Windows OS.

## 2. Strict Architectural Constraints
* **NO WEB VIEWS:** Do not use WebView2, Electron, Tauri, or Blazor Hybrid. The UI must be constructed using native WinUI 3 XAML.
* **MEMORY CONSERVATION:** The app must be compatible with .NET Native AOT. Avoid heavy reflection, dynamic code generation, or bloated dependencies that break AOT compilation.
* **BACKGROUND EFFICIENCY:** Do not use polling loops (`while(true) { Thread.Sleep() }`) for OS events. You must rely on native Windows Event Hooks, WMI events, and OS interrupts.

## 3. Implementation Rules
* **Win32 Interop:** When wrapping Win32 APIs (like `SetWindowDisplayAffinity` or `GetExtendedTcpTable`), use `LibraryImport` for AOT compatibility instead of the older `DllImport`.
* **Threading:** Any UI updates triggered by the local webhook server or OS events MUST be dispatched back to the main UI thread using `DispatcherQueue`.
* **Animations:** Use `Microsoft.UI.Composition` (Visual Layer) for smooth, 60fps+ spring physics when the notch expands or contracts. Do not rely solely on basic XAML storyboards if they drop frames during resizing.
* **State Management:** Keep it localized. Avoid heavy MVVM frameworks (like Prism) to keep binary size small; use simple `INotifyPropertyChanged` records.

## 4. Code Generation Protocol
* Always write complete, functional methods rather than placeholders.
* When adding a new capability, create an isolated service class (e.g., `ClipboardMonitorService.cs`, `MediaControlService.cs`).
* Assume the user is executing this code directly on a Windows 11 machine with developer mode enabled.