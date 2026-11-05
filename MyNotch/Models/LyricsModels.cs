using System.Text.Json.Serialization;

namespace MyNotch.Models;

/// <summary>One timed lyric line from an LRC file.</summary>
public sealed record LrcLine(TimeSpan Time, string Text);

/// <summary>Source-generated LRCLIB deserialization — no reflection, Native AOT compatible.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LrclibResponse))]
[JsonSerializable(typeof(LrclibResponse[]))]
internal sealed partial class LrclibJsonContext : JsonSerializerContext
{
}

/// <summary>Shared LRCLIB get/search record. Case-insensitive source generation maps API field names.</summary>
internal sealed record LrclibResponse
{
    public string TrackName { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string? AlbumName { get; init; }
    public double Duration { get; init; }
    public bool Instrumental { get; init; }
    public string? PlainLyrics { get; init; }
    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; init; }
}
