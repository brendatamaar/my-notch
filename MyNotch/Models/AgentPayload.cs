using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyNotch.Models;

public enum AgentStatus
{
    Unknown,
    Running,
    AwaitingApproval,
    Done,
    Error,
}

/// <summary>Webhook payload: {"agent":"claude-code","status":"awaiting_approval","message":"Execute script?","pid":1234}</summary>
public sealed record AgentPayload
{
    public string Agent { get; init; } = string.Empty;

    [JsonConverter(typeof(AgentStatusConverter))]
    public AgentStatus Status { get; init; } = AgentStatus.Unknown;

    public string Message { get; init; } = string.Empty;

    public int? Pid { get; init; }

    public long? Hwnd { get; init; }

    /// <summary>Optional session id: several sessions of one agent are tracked separately.</summary>
    public string? Session { get; init; }

    /// <summary>Optional working directory; its folder name labels the session and feeds the repository list.</summary>
    public string? Cwd { get; init; }

    [JsonIgnore]
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    public bool IsValid => !string.IsNullOrWhiteSpace(Agent);

    /// <summary>Local "HH:mm" for list rows.</summary>
    [JsonIgnore]
    public string TimeText => ReceivedAt.ToLocalTime().ToString("HH:mm");
}

/// <summary>Unknown status strings map to Unknown instead of failing deserialization. Hand-written: no reflection, AOT-safe.</summary>
internal sealed class AgentStatusConverter : JsonConverter<AgentStatus>
{
    public override AgentStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return AgentStatus.Unknown;
        }

        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "running" => AgentStatus.Running,
            "awaiting_approval" or "awaitingapproval" or "awaiting-approval" => AgentStatus.AwaitingApproval,
            "done" or "completed" or "success" => AgentStatus.Done,
            "error" or "failed" or "failure" => AgentStatus.Error,
            _ => AgentStatus.Unknown,
        };
    }

    public override void Write(Utf8JsonWriter writer, AgentStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            AgentStatus.Running => "running",
            AgentStatus.AwaitingApproval => "awaiting_approval",
            AgentStatus.Done => "done",
            AgentStatus.Error => "error",
            _ => "unknown",
        });
}
