namespace MyNotch.Models;

/// <summary>
/// Closed music display: only the timed lyric (previewing the first/next line during an intro or a gap).
/// Without a synced lyric the text is empty and the notch shows just artwork and beat bars, never title/artist.
/// </summary>
public readonly record struct MusicGlance(string Text, bool IsLyric)
{
    public static MusicGlance Resolve(bool synced, string current, string next)
    {
        static bool Meaningful(string line) => !string.IsNullOrWhiteSpace(line) && line.Trim() != "♪";
        if (synced && Meaningful(current)) return new(current.Trim(), true);
        if (synced && Meaningful(next)) return new(next.Trim(), true);
        return new(string.Empty, false);
    }
}
