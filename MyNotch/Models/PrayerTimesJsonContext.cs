using System.Text.Json.Serialization;

namespace MyNotch.Models;

/// <summary>Source-generated Aladhan deserialization — no reflection, Native AOT compatible.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AladhanDayResponse))]
internal sealed partial class PrayerTimesJsonContext : JsonSerializerContext
{
}

/// <summary>Shape: {"data":{"timings":{...},"date":{...}}} — only timings are modeled.</summary>
internal sealed record AladhanDayResponse(
    [property: JsonPropertyName("data")] AladhanDayData? Data);

internal sealed record AladhanDayData(
    [property: JsonPropertyName("timings")] AladhanTimingsJson? Timings);

internal sealed record AladhanTimingsJson
{
    public string? Imsak { get; init; }
    public string? Fajr { get; init; }
    public string? Sunrise { get; init; }
    public string? Dhuhr { get; init; }
    public string? Asr { get; init; }
    public string? Maghrib { get; init; }
    public string? Isha { get; init; }
}
