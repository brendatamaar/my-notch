using System.Text.Json.Serialization;

namespace MyNotch.Models;

/// <summary>Source-generated JSON serialization — no reflection, Native AOT compatible (AGENTS.md).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AgentPayload))]
internal sealed partial class WebhookJsonContext : JsonSerializerContext
{
}
