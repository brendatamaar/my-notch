using System.Text.Json.Serialization;

namespace MyNotch.Models;

internal sealed record CalendarTodoDocument(int Version, CalendarTask[] Items);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CalendarTodoDocument))]
internal sealed partial class CalendarTodoJsonContext : JsonSerializerContext { }
