namespace MyNotch.Models;

public enum ClipboardKind
{
    None,
    Jwt,
    Json,
    Timestamp,
    Uuid,
    Base64,
    Url,
    Color,
    Cron,
}

/// <summary>Result of analyzing one clipboard text snapshot (PLAN Phase 3, PRD §3.4).</summary>
public sealed record ClipboardDetection
{
    public ClipboardKind Kind { get; init; } = ClipboardKind.None;
    public string Preview { get; init; } = string.Empty;
    public string Formatted { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    /// <summary>0xAARRGGBB for <see cref="ClipboardKind.Color"/>.</summary>
    public uint? ColorArgb { get; init; }

    public bool HasAction => Kind != ClipboardKind.None;

    public string ActionLabel => Kind switch
    {
        ClipboardKind.Jwt => "Decode JWT",
        ClipboardKind.Json => "Format JSON",
        ClipboardKind.Timestamp => "Convert timestamp",
        ClipboardKind.Uuid => "Inspect UUID",
        ClipboardKind.Base64 => "Decode Base64",
        ClipboardKind.Url => "Decode URL",
        ClipboardKind.Color => "Convert color",
        ClipboardKind.Cron => "Explain cron",
        _ => string.Empty,
    };

    public static ClipboardDetection None { get; } = new();
}

/// <summary>One entry of the in-memory clipboard history (never written to disk).</summary>
public sealed record ClipboardClip(string Text, string Preview, string KindLabel, DateTimeOffset At)
{
    public string TimeText => At.LocalDateTime.ToString("HH:mm");
    public bool HasKind => KindLabel.Length > 0;
}
