namespace MyNotch.Models;

/// <summary>Detached snapshot of the current system media session (PLAN Phase 4, PRD §3.5).</summary>
public sealed record MediaTrackInfo
{
    public string Title { get; init; } = string.Empty;
    public long Revision { get; init; }
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string SourceApp { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public bool HasSession { get; init; }

    /// <summary>Encoded album art (PNG/JPEG bytes) from the session, if the player provides one.</summary>
    public byte[]? Thumbnail { get; init; }

    public MediaTimeline Timeline { get; init; } = MediaTimeline.Empty;

    public string DisplayText => !HasSession
        ? "No active media"
        : string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} — {Artist}";

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>Same song regardless of play state or position (used to know when to refetch lyrics).</summary>
    public bool IsSameTrack(MediaTrackInfo other) =>
        HasSession == other.HasSession && Title == other.Title && Artist == other.Artist && Album == other.Album && SourceApp == other.SourceApp;

    public static MediaTrackInfo NoMedia { get; } = new() { HasSession = false };
}

/// <summary>
/// Position report from SMTC. <see cref="PositionNow"/> extrapolates from the report time,
/// so nothing has to poll to know where the song is.
/// </summary>
public sealed record MediaTimeline
{
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset ReportedAt { get; init; } = DateTimeOffset.UtcNow;
    public double Rate { get; init; } = 1.0;

    public bool IsKnown => Duration > TimeSpan.Zero;

    public TimeSpan PositionNow(bool isPlaying)
    {
        var pos = Position;
        if (isPlaying) pos += (DateTimeOffset.UtcNow - ReportedAt) * Rate;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (IsKnown && pos > Duration) pos = Duration;
        return pos;
    }

    public static MediaTimeline Empty { get; } = new();
}
