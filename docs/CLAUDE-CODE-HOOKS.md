# Claude Code integration

MyNotch can show every Claude Code session and answer its permission prompts from the notch. The integration uses Claude Code's command hooks, and `curl.exe` (built into Windows 10+) forwards each hook's stdin to the local webhook.

## Setup

1. Open MyNotch → **Settings** → **Copy Claude Code hooks**. You can also copy the block below.
2. Merge it into the `"hooks"` object of `~/.claude/settings.json`, or into a project's `.claude/settings.json`.
3. Restart Claude Code.

```json
{
  "hooks": {
    "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "5", "-o", "NUL", "--data-binary", "@-", "http://127.0.0.1:9999/claude/hook"], "timeout": 10 } ] } ],
    "Notification":     [ { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "5", "-o", "NUL", "--data-binary", "@-", "http://127.0.0.1:9999/claude/hook"], "timeout": 10 } ] } ],
    "Stop":             [ { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "5", "-o", "NUL", "--data-binary", "@-", "http://127.0.0.1:9999/claude/hook"], "timeout": 10 } ] } ],
    "SessionEnd":       [ { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "5", "-o", "NUL", "--data-binary", "@-", "http://127.0.0.1:9999/claude/hook"], "timeout": 10 } ] } ],
    "PermissionRequest": [
      { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "600", "--data-binary", "@-", "http://127.0.0.1:9999/claude/permission"], "timeout": 600 } ] }
    ]
  }
}
```

## What each hook does

| Hook | Notch state |
| --- | --- |
| `UserPromptSubmit` | Running, "Working on: <first line of the prompt>" |
| `Notification` (`permission_prompt`, `elicitation_dialog`) | Awaiting approval |
| `Notification` (`idle_prompt`) | Done, "Waiting for your input" |
| `Stop` | Done, with the first line of Claude's last message |
| `StopFailure` (optional) | Error |
| `PreToolUse` / `PostToolUse` (optional, noisy) | Running, "Using <tool>" |
| `SessionEnd` | Removes the session |

Each Claude Code `session_id` is tracked separately; the Agents tab lists sessions once there are two or more, and the closed notch shows `Claude Code +N`. The session's folder name comes from `cwd`, and its repository appears under **Dev → Repositories**. MyNotch looks up the TCP connection to find which terminal window ran the hook (Windows Terminal, VS Code, …), so clicking a session focuses that window.

## Permission prompts

`/claude/permission` keeps the HTTP request open until you choose in the notch:

- **Allow** returns `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}`.
- **Deny** returns the same with `"behavior":"deny"` and a message.
- **Ask in terminal**, the configured wait running out (Settings → Agents, default 60 s), or MyNotch closing all return an empty body. Claude Code then shows its normal prompt.

Claude Code still applies its own deny/ask rules after a hook allows. Turning off **Answer Claude Code permission prompts here** makes the endpoint answer "no decision" right away.

If MyNotch isn't running, `curl.exe` fails to connect and Claude Code reports a non-blocking hook error. The permission flow carries on as normal.

## Other agents

Any tool can still POST the original payload to `/agent`:

```json
{"agent":"my-agent","status":"awaiting_approval","message":"Deploy?","session":"optional-id","cwd":"C:\\src\\app","pid":1234}
```

`status`: `running`, `awaiting_approval`, `done`, `error`. Requests with a browser `Origin` header are rejected (403), so web pages can't fake alerts.
