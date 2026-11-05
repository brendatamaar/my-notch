using System.Text.Json.Serialization;

namespace MyNotch.Models;

/// <summary>Source-generated commands.json deserialization — no reflection, Native AOT compatible.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<SavedCommand>))]
internal sealed partial class CommandsJsonContext : JsonSerializerContext
{
}
