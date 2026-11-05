# Focus and Prayer

Focus uses its own compact 600 x 200 DIP layout. Its two columns combine Oyenza's progress ring and primary timer controls with Nusa's four-session round overview. The left ring shows the countdown and phase end time; the right shows phase selection, Start/Pause/Resume, Reset, Skip, session segments and remaining round time. The accent is violet, with a green long-break segment.

Defaults: 25-minute Focus, 5-minute Short break, 15-minute Long break after four focus sessions. Focus completion starts its break automatically. A completed break readies the next Focus session without starting it, so a long sleep cannot manufacture completed focus sessions. Pause freezes the countdown; resume continues it. Reset restores the selected phase's full duration and stops it. Skip is disabled and is a no-op while idle; while running or paused it moves to the next phase without adding a completed focus session. The completed count belongs to this app run, not a persisted daily total.

The pure FocusTimer.Step takes a FocusTimerState, input and configuration, and returns a state plus completed-phase notifications. Toggle, Select, Reset, Skip and Tick are explicit inputs. Remaining time stays positive and bounded by the selected duration; rounds stay within their configured bounds. Delayed ticks carry overshoot into the following break. Idle and paused states ignore ticks. The dispatcher shell supplies measured Stopwatch elapsed time and runs its UI timer only while running.

Prayer is now a separate moon-icon tab. It displays the next prayer and five daily prayer times from the existing Aladhan service (current default location: Jakarta). Existing prayer reminders and collapsed-glance priorities are retained; prayer no longer occupies the Focus page.

Regression tests exercise pause/resume, overshoot, short/long breaks, all four sessions, reset, skip, long sleep and 15,625 input sequences. Removing the running-state guard was verified to fail the paused-timer regression before restoring it. Run with the existing regression executable:

dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj

References:
- https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni/src/pages/focus.rs
- https://rama2555.github.io/nusa/
- User-supplied Nusa focus screenshot, Screenshot 2026-11-04 172959.png

Live visual verification is still manual; the desktop automation runtime was unavailable earlier in this session.

Round bars now track earned completion bits instead of inferring completed sessions from the selected phase/round number. Choosing or running a manual short/long break cannot fill a Focus segment. The current running/paused Focus segment shows elapsed progress; skipped or reset unfinished work is not credited. Finished Focus segments remain filled through their break and clear when the long break finishes and a new round begins. The large timer ring still shows elapsed progress for whichever phase is running, including a break.

Layout update: this tab is 600 x 200 DIPs and independent of AI usage height. Main tabs have no page scrolling.
