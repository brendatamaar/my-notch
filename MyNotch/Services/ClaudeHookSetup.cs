namespace MyNotch.Services;

/// <summary>The Claude Code settings.json "hooks" block that connects Claude Code to MyNotch (see docs/CLAUDE-CODE-HOOKS.md).</summary>
public static class ClaudeHookSetup
{
    public static string SettingsJson
    {
        get
        {
            int port = WebhookServerService.Port;
            string status = $$"""
                { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "5", "-o", "NUL", "--data-binary", "@-", "http://127.0.0.1:{{port}}/claude/hook"], "timeout": 10 } ] }
                """.Trim();
            return $$"""
                {
                  "hooks": {
                    "UserPromptSubmit": [ {{status}} ],
                    "Notification": [ {{status}} ],
                    "Stop": [ {{status}} ],
                    "SessionEnd": [ {{status}} ],
                    "PermissionRequest": [
                      { "hooks": [ { "type": "command", "command": "curl.exe", "args": ["-s", "-m", "600", "--data-binary", "@-", "http://127.0.0.1:{{port}}/claude/permission"], "timeout": 600 } ] }
                    ]
                  }
                }
                """;
        }
    }
}
