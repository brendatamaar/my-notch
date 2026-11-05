# Music, transport and automatic lyrics

The 600 DIP-wide Music panel with a height of 204 DIPs follows the supplied Oyenza/Nusa references: album artwork on the left, track metadata/progress/transport in the middle, and lyric context on the right. Play/pause uses a white circle and black vector shapes; skip buttons use white vectors. Playback-provider labels (Apple Music, Spotify, etc.) identify the player, not the lyrics service.

## Automatic lookup

There is no manual fetch/retry control. Every track/session change cancels the old request and any scheduled retry. Metadata is normalized before display and lookup, including Apple Music's Artist — Album field and featured-artist title decorations.

LyricsService first searches LRCLIB's existing catalogue, conservatively matching normalized title/primary artist, album and duration. A synced catalogue hit returns immediately. Plain catalogue results are retained while exact lookups with album/duration, without album, and without duration look for a synced edition. A large timing mismatch is shown as plain lyrics rather than falsely synchronized lyrics.

A bounded 32-song LRU memory cache keeps synced, plain and instrumental results. Keys include normalized title, artist, album and rounded duration. Returning to a cached song avoids network access. Failures and misses are not cached.

## Pure lifecycle

LyricsLookup.Step(state, input) returns a state and an optional command. IO, timers and cancellation live in the shell.

| Input | State | Result |
| --- | --- | --- |
| Request(query) | Any | Loading, new generation, FetchLyrics |
| Completed(current attempt, transient failure/miss) | Loading, fewer than two retries | Waiting, ScheduleLyricsRetry |
| RetryDue(current attempt) | Waiting | Loading, new generation, FetchLyrics |
| Completed(current attempt, success/permanent failure/exhausted) | Loading | Finished |
| Reset | Any | Idle, fresh generation |
| Stale/duplicate completion or retry | Any other attempt/state | Unchanged |

At most two automatic retries follow the initial lookup (2 seconds then 8 seconds). A server cooldown overrides the shorter backoff. New track changes and shutdown cancel waiting work, and attempt checks fence already-queued stale events. There is no periodic idle polling. Once exhausted, the panel shows an unavailable/error state without requiring a manual action.

## Next/previous

Transport uses Windows media-session commands with a three-second timeout. It reads actual metadata immediately afterwards and once more after 1.2 seconds, like Oyenza's post-command resync. A newer transport command cancels the old resync. Player-declined commands are not treated as successful song changes.

Oyenza additionally uses Apple Music's own UI for Previous when the Windows session refuses it. This native UI-automation fallback is not currently implemented here. The resync does not claim to bypass a player-declined command.

## Providers

LRCLIB remains the automatic lyrics provider without account setup. Oyenza also supports Apple's authenticated lyrics API using a user-supplied media-user-token and TTML. MyNotch does not obtain that token from the desktop app and does not currently implement this authenticated provider. Removing LRCLIB would remove the working no-login provider rather than make retrieval automatic.

No user-visible offset control or offset label is present in the current XAML. Any reported offset label still needs its exact text/location to identify.

## Artwork

ArtworkLookup (Idle, Loading, Ready, Missing) keys images by player, artist, title and album. Property updates bypass the same-track cache; playback-only events may reuse it. Refresh revisions and bitmap generations reject late results. A single 700 ms check after a song change catches late artwork, without an idle polling loop.

## Verification

Run: dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj

Regression coverage includes Apple Music metadata normalization, collaborator credits, catalogue-first lookup, plain fallback, A -> B -> A cache reuse, bounded cache eviction, automatic retry/cooldown/exhaustion, stale retry/response rejection, artwork updates and missing images. Exhaustive sequences check replay and lifecycle invariants. Removing the retry limit or artwork-refresh bypass is checked to cause a failing regression before restoring it.

Read-only live probe: dotnet run --project tests/MyNotch.MediaProbe/MyNotch.MediaProbe.csproj

Add --artwork to report thumbnail revisions/fingerprints. Add --lookup artist title duration [album] to replay captured metadata against live LRCLIB and compare cold and cached lookup times. A captured Apple Music query for Dongker / Luka di Pelupuk Mata (feat. Binar), Ceriwis Necis, 291 seconds returned 66 timed lines after normalization.

References:
- https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni/src/services/media.rs
- https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni/src/services/lyrics.rs
- https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni/src/services/applemusic.rs
- https://github.com/lucacadalora/oyenza-studio/blob/main/crates/poni-core/src/nowplaying.rs
- https://lrclib.net/docs.txt

## Closed music lyrics

While music is playing and timed lyrics are available, the closed notch displays the active lyric in a 380 x 48 DIP glance with up to two lines and a short fade on line changes. The small album cover and playback bars remain beside it. Before the first timed line or during a blank timed line, it previews the next lyric instead of showing only a music-note marker. When synced lyrics are unavailable, it uses track title/artist in the normal 304 x 38 DIP music glance. Plain-only lyrics are not presented as a synchronized active line. The existing agent/focus/notice priority remains unchanged.

Layout update: this tab is 600 x 204 DIPs and independent of AI usage height. Main tabs have no page scrolling.
