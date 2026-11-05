# Attached notch design

References reviewed: https://github.com/lucacadalora/oyenza-studio and https://rama2555.github.io/nusa/

The black reference themes guide the appearance: a body flush with the top screen edge, concave shoulders, rounded bottom corners, white controls and muted secondary text. This is an adaptation for MyNotch's existing WinUI features, rather than a copy of either application's full UI.

The closed clock is 150 x 30 DIPs. Contextual glances expand horizontally. The open panel is 600 DIPs wide with 32 DIP bottom corners. Each tab fits its own content: Music 204 DIPs (180 without a session), Focus 200, Prayer 202. Prayer columns align to the top below the header, avoiding large empty bands. AI usage has a separate 200 DIP minimum and grows to fit its cards/activity without page scrolling. Its measured height never propagates to Music, Focus or Prayer. Activity history is paginated two entries at a time. Dev/Run remain 200 DIPs. Plain unsynced lyrics show a bounded excerpt without a scroll area. Tab switches animate the shape and resize the native window through the same path as opening/collapsing. A shared animated composition geometry drives the body, clip and shadow; the shoulders follow its width and corner radius through expressions. Extending that geometry above the screen keeps the top edge straight throughout transitions. Content is never stretched with the shape.

Verification: Release build passed without warnings or errors; the redesigned Release process remained running after launch. Live visual and interaction verification remains outstanding because both desktop automation runtimes timed out.

Navigation follows the supplied Oyenza screenshot: persistent 16 DIP line icons aligned to the top left, with a gray capsule containing the icon and page name for the selected tab. Inactive tabs show muted icons. There is no dot mode or hover reveal. Activity badges, tooltips, keyboard access and wheel navigation remain available.
The header shows the date and a 24-hour clock on the right. Its one-shot timer updates at each minute boundary while expanded and stops on collapse and shutdown.

Idle update: the closed 150 x 30 DIP notch now displays two white eyes in place of the clock. The eyes are 6.5 x 11 DIPs with centers 22 DIPs apart, following [Oyenza's idle eyes](https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni/src/activities.rs). They blink for 140 ms every 5.5 seconds using a compositor animation, which stops when expanded, when another glance appears, and at shutdown. The header's bell and close buttons have been removed; notification preferences remain in the right-click/tray menu.

Music uses a dedicated right-hand lyrics column, vector transport buttons and elapsed/remaining times. See [MUSIC.md](MUSIC.md) for the lookup lifecycle, fallbacks and verification.

Interaction update: notch actions use click controls. The global Alt+Space registration, custom Escape/Enter command handlers and command-filter text box are removed. Actions lists saved targets for clicking; the header has an explicit close button. Calendar task titles remain text content; adding a task uses +, not an Enter shortcut. Native accessibility input is retained by Windows controls.
