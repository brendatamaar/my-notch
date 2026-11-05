using System.Text;
using System.Text.Json;

namespace MyNotch.Models;

/// <summary>What a Claude Code hook event means for the notch.</summary>
public abstract record ClaudeHookEvent
{
    public sealed record Update(AgentPayload Payload) : ClaudeHookEvent;
    public sealed record End(string SessionKey) : ClaudeHookEvent;
    public sealed record Ignore : ClaudeHookEvent;
}

public enum ApprovalDecision { None, Allow, Deny }

/// <summary>A permission prompt Claude Code is about to show, forwarded by its PermissionRequest hook.</summary>
public sealed record ApprovalRequest(string Id, string Agent, string Session, string Cwd, string Tool, string Detail, DateTimeOffset At)
{
    public int? Pid { get; init; }
    public long? Hwnd { get; init; }
    public string Project => string.IsNullOrWhiteSpace(Cwd) ? "" : Path.GetFileName(Cwd.TrimEnd('\\', '/'));
}

/// <summary>
/// Pure mapping between Claude Code's hook JSON (stdin of a command hook) and MyNotch state.
/// See docs/CLAUDE-CODE-HOOKS.md for the settings.json snippet.
/// </summary>
public static class ClaudeHooks
{
    public const string AgentName = "Claude Code";

    public static ClaudeHookEvent Map(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return new ClaudeHookEvent.Ignore();
        string hook = Text(root, "hook_event_name");
        string session = Text(root, "session_id");
        string cwd = Text(root, "cwd");
        AgentPayload Payload(AgentStatus status, string message) => new()
        {
            Agent = AgentName, Status = status, Message = Clip(message, 160), Session = session.Length > 0 ? session : null,
            Cwd = cwd.Length > 0 ? cwd : null, ReceivedAt = now,
        };

        switch (hook)
        {
            case "UserPromptSubmit":
                string prompt = FirstLine(Text(root, "prompt"));
                return new ClaudeHookEvent.Update(Payload(AgentStatus.Running, prompt.Length > 0 ? "Working on: " + prompt : "Working…"));
            case "PreToolUse":
            case "PostToolUse":
                string tool = Text(root, "tool_name");
                return new ClaudeHookEvent.Update(Payload(AgentStatus.Running, tool.Length > 0 ? "Using " + tool : "Working…"));
            case "PermissionRequest":
                return new ClaudeHookEvent.Update(Payload(AgentStatus.AwaitingApproval, $"Allow {Text(root, "tool_name")}?"));
            case "Notification":
                string message = Text(root, "message");
                return Text(root, "notification_type") switch
                {
                    "permission_prompt" or "elicitation_dialog" => new ClaudeHookEvent.Update(Payload(AgentStatus.AwaitingApproval, message.Length > 0 ? message : "Needs your attention")),
                    "idle_prompt" => new ClaudeHookEvent.Update(Payload(AgentStatus.Done, message.Length > 0 ? message : "Waiting for your input")),
                    _ => new ClaudeHookEvent.Ignore(),
                };
            case "Stop":
                string last = FirstLine(Text(root, "last_assistant_message"));
                return new ClaudeHookEvent.Update(Payload(AgentStatus.Done, last.Length > 0 ? last : "Finished"));
            case "StopFailure":
                return new ClaudeHookEvent.Update(Payload(AgentStatus.Error, "Stopped with an error"));
            case "SessionEnd":
                return session.Length > 0 ? new ClaudeHookEvent.End(AgentSessions.KeyOf(Payload(AgentStatus.Unknown, ""))) : new ClaudeHookEvent.Ignore();
            default:
                return new ClaudeHookEvent.Ignore();
        }
    }

    public static ApprovalRequest? ParsePermission(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string tool = Text(root, "tool_name");
        if (tool.Length == 0) return null;
        root.TryGetProperty("tool_input", out var input);
        return new ApprovalRequest(Guid.NewGuid().ToString("N"), AgentName, Text(root, "session_id"), Text(root, "cwd"), tool, Describe(tool, input), now);
    }

    /// <summary>The most useful single line for a tool call: the command, file, URL or query.</summary>
    public static string Describe(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        foreach (var field in tool switch
        {
            "Bash" or "PowerShell" => new[] { "command" },
            "Edit" or "MultiEdit" or "Write" or "Read" or "NotebookEdit" => new[] { "file_path", "notebook_path" },
            "WebFetch" => new[] { "url" },
            "WebSearch" => new[] { "query" },
            "Glob" or "Grep" => new[] { "pattern" },
            "Task" or "Agent" => new[] { "description", "prompt" },
            _ => Array.Empty<string>(),
        })
        {
            string value = Text(input, field);
            if (value.Length > 0) return Clip(value, 600);
        }
        return Clip(input.GetRawText(), 600);
    }

    /// <summary>stdout for the PermissionRequest hook; empty means "no decision" (Claude Code shows its own prompt).</summary>
    public static string Response(ApprovalDecision decision)
    {
        if (decision == ApprovalDecision.None) return "";
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WritePropertyName("hookSpecificOutput");
            json.WriteStartObject();
            json.WriteString("hookEventName", "PermissionRequest");
            json.WritePropertyName("decision");
            json.WriteStartObject();
            json.WriteString("behavior", decision == ApprovalDecision.Allow ? "allow" : "deny");
            if (decision == ApprovalDecision.Deny) json.WriteString("message", "Denied from MyNotch");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim().TrimStart('#', '*', '-', ' ');
            if (t.Length > 0) return Clip(t, 160);
        }
        return "";
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
