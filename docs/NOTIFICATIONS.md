# Notch notifications

Notifications use native WinUI and remain local. The date/time header has no notification or collapse buttons. Notification preferences remain available through the right-click/tray menu > Notification settings, with Recent (four entries per page, up to forty in memory). Contents are not written to disk. Category preferences are stored in `%LOCALAPPDATA%/MyNotch/notification-settings.json`.

## Behavior

| Source | Trigger | Delivery |
|---|---|---|
| Focus / prayer | Existing timer completion and prayer-time events | Existing durations preserved |
| To-do | Optional clock time set with the clock button on a calendar task | One-shot timer; completed/deleted tasks excluded; delivery persisted; catches up at most 24 hours |
| AI quota | Fresh successful provider reading reaches 90%, then 100% | Once per threshold/reset cycle; rearmed below 80%; existing per-provider cooldowns apply; background refresh every five minutes |
| CPU / RAM | At least 90% continuously for 30 seconds | Rearmed below 80%; lightweight native counter sample every five seconds; no background process/GPU scan |
| Windows apps | New toast after consent | Public `UserNotificationListener` events; old notifications not replayed |
| Bluetooth | Paired Classic/BLE device connects/disconnects | AEP watcher; initial enumeration is silent |
| Volume | Default playback endpoint volume/mute changes | Core Audio callback, rebound when default output changes |
| Brightness | Display driver reports new panel brightness | `RegisterPowerSettingNotification` / `GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS`; initial value silent; external monitors may not expose it |

The closed notch shows the icon, title and message; volume/brightness include a level bar. Notifications appear only while the notch is idle/collapsed; expanded pages have no notification banner. A queue holds at most sixteen pending items, expires stale backlog after two minutes, and coalesces rapid HUD changes. Agent approval retains its existing glance priority. After the notice expires, the idle notch returns to its normal glance automatically.

## Windows app access

Windows requires package identity, the `userNotificationListener` capability, and explicit OS consent. The default unpackaged executable has no identity. Right-click/tray menu > Notification settings > Set up Windows access registers a local development identity using the bundled `Setup-NotificationAccess.ps1`; it never changes Developer Mode or installs a trust certificate. The machine must permit development-package registration. After setup, close MyNotch and launch its Start-menu entry, then click Enable Windows app notifications and grant the Windows prompt. Denied access can be changed in the Windows notification privacy settings opened from the same panel.

The setup uses an external-location manifest pointing at the current build folder. Re-register when changing that folder. Do not move/delete a registered build directory while using that identity. Removal, if needed: remove the MyNotch.Local app registration through Windows Apps settings. Registration and consent failures leave the other notification types working.

## State and verification

`NotificationRules.Step` handles empty/displaying/queued states and receive/advance/dismiss inputs with an injected timestamp. Threshold functions handle healthy/sustained-high/warned and quota bands without IO. The hub interprets one-shot display timers; separate services own Bluetooth, Core Audio and power subscriptions and release them at shutdown. Task reminders share the existing atomic file store. OS callbacks enqueue UI changes through `DispatcherQueue`.

Run the dependency-free console tests for queue bounds/coalescing, threshold hysteresis, reminder scheduling, legacy task-file compatibility, persisted delivery and stale delivery guards. Windows hardware event delivery still needs manual checks with the actual Bluetooth device and brightness-capable display.

Verification on this machine: Debug and Release builds and the full console suite passed; startup confirmed successful Core Audio registration, Bluetooth watcher startup, brightness registration and construction of both notification flyout views. Unsigned identity registration passed manifest validation but Windows rejected installation with `0x80073CFF` because development/sideloading policy is disabled. No OS policy was changed.

Native AOT publishing compiles, with warnings in the Windows App SDK's transitive WebView2 projection (the app does not use WebView). The published native executable currently fails during WinUI startup with `0xc0000374`. An opt-in `MYNOTCH_STARTUP_TRACE` confirmed App XAML initialization completed but `OnLaunched`/MainWindow creation was never reached. This remains an unresolved publish limitation; use the regular Debug/Release build for manual verification. This observation does not establish when the publish issue was introduced.

Sources: [Windows notification listener](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener), [package identity](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps), [Core Audio callback](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolumecallback), [power notifications](https://learn.microsoft.com/en-us/windows/win32/power/registering-for-power-events).
