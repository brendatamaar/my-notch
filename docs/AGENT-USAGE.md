# Agents and account usage

The Agents tab now uses a two-column card layout inspired by the supplied Oyenza screenshot. Codex and Antigravity are on the left; Claude Code and Muse Code on the right. Thin bars show **used** percentages, with amber from 75% and coral from 90%. Reset times use the Windows local timezone. All provider rows remain visible without scrolling. AI usage has its own 200 DIP minimum height which grows to fit the quota cards and current activity page; compact Music/Focus/Prayer sizes remain independent. The Services menu shows/hides providers for this app session. Existing webhook notifications, sender focusing, history and collapsed attention behavior remain intact below the quota cards. The HTTP POST endpoint hint is intentionally absent from the UI.

## Data sources

- Codex: installed `codex.exe app-server --listen stdio://`; initialize/initialized handshake followed only by `account/rateLimits/read`. No thread or model turn is created. Prefer the `codex` bucket, not another model's quota. Uses the CLI's existing account and auth handling.
- Claude Code: reads `CLAUDE_CONFIG_DIR/.credentials.json` or `~/.claude/.credentials.json`, then GET `https://api.anthropic.com/api/oauth/usage` with its access token and the `oauth-2025-04-20` beta header. No token is printed, cached by MyNotch, refreshed, or written. Supports both flat windows and the newer limits array. A 401 asks the user to sign in again in Claude Code.
- Antigravity: check `agy --version` first; versions before 1.1.11 are refused because /usage could become a model prompt. On supported versions execute `agy -p /usage --output-format json`. Only a successful usage command with no model turn/conversation is accepted. Remaining fractions become used percentages. Disabled pools are skipped.
- Muse Code: execute the installed native binary's `usage --json` command, never a model prompt. On Windows the launcher directory's `.muse-version` chooses `muse-bin-VERSION.exe` so viewing usage does not invoke the install/update launcher. Parse allowance tier, window/weekly usedPercent, resetsAtMs and asOfMs. An absent allowance is shown as unavailable, never as 0%.

CLI processes are hidden, bounded to 45 seconds, and killed on timeout/shutdown. Standard output/error are bounded and never logged. Local credentials are only read; MyNotch doesn't log out, replace, or manually refresh provider sessions. No third-party usage collector receives account data.

## Refresh behavior

Opening Agents refreshes due services. While Agents is visible a two-minute UI timer checks which services are due; the timer stops on collapse/tab change. Minimum successful refresh gaps: Codex/Claude two minutes, Muse five minutes, Antigravity fifteen minutes. Refresh respects the same gaps. Network failures wait 30 seconds, HTTP rate limiting at least five minutes or Retry-After if later; Antigravity always waits at least fifteen minutes. Each provider is single-flight; late/duplicate results are fenced by generation. Previous readings survive failures with an explicit previous-reading label. A passed reset is not silently converted into 0% usage. Data is cached in memory only, so no old account snapshot survives restart. No background low-quota alert toggle is shown because this page checks usage only while open.

## Verified on this computer

Codex and Antigravity return live account windows. Claude usage also returns live windows in the latest check (the earlier 401 was resolved by the provider session). Muse's official CLI returns `allowance: null` / `server_sent_no_usage` and states this can occur before first use in a new window. MyNotch surfaces these source conditions; it cannot infer an allowance that the server did not return.

## Validation

`dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj`

Includes provider parser fixtures, missing/invalid values, used-vs-remaining conversion, rejected model-turn output, millisecond timestamps, generation fencing, retained previous readings, 4096 lifecycle sequences, single-flight, provider cooldowns, Retry-After, disabling providers and cancellation.

Opt-in live adapter check (prints quota rows and statuses only):
`dotnet run --project tests/MyNotch.Lyrics.Tests/MyNotch.Lyrics.Tests.csproj -- --usage-probe`

Manual QA: open Agents; verify four cards and per-provider source status, verify all Antigravity pools and use activity Previous/Next; toggle Services; collapse during refresh; reopen and check cooldowns. Re-login in Claude Code outside MyNotch, then refresh after the brief failure cooldown. Use Muse normally and retry once its server reports an allowance.

References:
- https://github.com/lucacadalora/oyenza-studio/tree/main/crates/studio-usage/src
- https://learn.chatgpt.com/docs/app-server
- https://antigravity.google/docs/cli/commands/usage
- Installed `muse usage --help` and its JSON output are the Muse schema authority used here.


Latest layout update: AI usage no longer uses a ScrollViewer. Only AI usage grows to fit its measured content above a 200 DIP minimum. Music/Focus/Prayer use their own compact 204/200/202 DIP heights. Activity history remains accessible with Previous/Next, two entries per page. The POST endpoint label stays hidden.

Quota rows put label, reset time and percentage on a single line above their bar; the updated stamp is available on the provider-name tooltip. This reduces the unscrolled content height while retaining every quota window.

## Update time and account-provided resets

The header shows the latest successful reading time (HH:mm:ss); each card shows its own update time. The Refresh button remains distinct from Reset limit. Oyenza's header reset-shaped icon is a refresh action, not quota redemption.

Codex's account/rateLimits/read reset-credit metadata is parsed. Reset limit appears only with an available, unexpired credit ID. A manual click invokes account/rateLimitResetCredit/consume and then reads actual usage again, bypassing the normal cooldown. No local progress bar is set to zero as a substitute. One reset is in flight through the refresh; the pending idempotency UUID is written to a per-credit local journal before the request so duplicate attempts/restarts reuse it. Confirmed non-consuming outcomes end the attempt and permit a new one later; unknown/consumed outcomes retain the UUID. Unknown network outcomes are labelled unconfirmed. Other providers do not get a fake reset action. No reset was consumed during development/verification: reset tests use a fake provider and the live probe is read-only.

Source: https://learn.chatgpt.com/docs/app-server (earned rate-limit resets).
