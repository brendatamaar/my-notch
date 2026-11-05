using System.Text.RegularExpressions;
using MyNotch.Models;
namespace MyNotch.Services;

internal static class LyricsMatching
{
    /// <summary>Player and catalogue durations often differ by intros, fades or encoder padding.</summary>
    public const double DurationToleranceSeconds = 15;
    /// <summary>Tight tolerance used when the artist can't be confirmed and only title + length identify the song.</summary>
    private const double StrictDurationSeconds = 3;

    public static string CleanTitle(string title)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var clean = Regex.Replace(title,
            @"\s*[\[(](?:official\s+)?(?:music\s+video|video|audio|lyrics?|lyric\s+video|visuali[sz]er)[\])]", string.Empty, options);
        clean = Regex.Replace(clean, @"\s*[\[(](?:feat\.?|ft\.?|with)\s+[^)\]]*[)\]]", string.Empty, options);
        clean = Regex.Replace(clean, @"\s+(?:feat\.?|ft\.?)\s+.*$", string.Empty, options);
        // "Song - Remastered 2011", "Song - 2011 Remaster", "Song - Radio Edit" style suffixes.
        clean = Regex.Replace(clean, @"\s+-\s+(?:\d{4}\s+)?(?:remaster(?:ed)?|re-?master(?:ed)?|mono|stereo|single version|radio edit|album version)\b.*$", string.Empty, options);
        return clean.Trim();
    }
    public static string PrimaryArtist(string artist)
    {
        var primary = artist.Split(new[] { ", ", "; ", " & ", " feat. ", " ft. ", " x " }, StringSplitOptions.None)[0].Trim();
        primary = Regex.Replace(primary, @"\s*-\s*topic$|\s*VEVO$|\s*Official$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return primary.Trim();
    }
    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static bool HasSynced(LrclibResponse record) => LrcParser.Parse(record.SyncedLyrics).Any(line => !string.IsNullOrWhiteSpace(line.Text));
    private static bool DurationOk(LrclibResponse record, LyricsQuery query, double tolerance) =>
        query.Duration <= TimeSpan.Zero ||
        (double.IsFinite(record.Duration) && Math.Abs(record.Duration - query.Duration.TotalSeconds) <= tolerance);

    public static LrclibResponse? Select(IEnumerable<LrclibResponse> records, LyricsQuery query)
    {
        var list = records as IList<LrclibResponse> ?? records.ToList();
        var titleKey = Key(CleanTitle(query.Title));
        var strict = list
            .Where(record => Key(CleanTitle(record.TrackName)) == titleKey)
            .Where(record => !string.IsNullOrWhiteSpace(query.Artist) && Key(PrimaryArtist(record.ArtistName)) == Key(PrimaryArtist(query.Artist)))
            .Where(record => DurationOk(record, query, DurationToleranceSeconds))
            .OrderByDescending(HasSynced)
            .ThenByDescending(record => !string.IsNullOrWhiteSpace(query.Album) && Key(record.AlbumName ?? "") == Key(query.Album))
            .ThenBy(record => Math.Abs(record.Duration - query.Duration.TotalSeconds)).FirstOrDefault();
        if (strict is not null) return strict;
        // Artist spelling differs between the player and the catalogue: trust an identical title only when
        // the track length also lines up tightly, and only for synced results.
        if (query.Duration <= TimeSpan.Zero) return null;
        return list
            .Where(record => Key(CleanTitle(record.TrackName)) == titleKey && HasSynced(record))
            .Where(record => DurationOk(record, query, StrictDurationSeconds))
            .OrderBy(record => Math.Abs(record.Duration - query.Duration.TotalSeconds)).FirstOrDefault();
    }
    public static LyricsResult Decode(LrclibResponse? record, TimeSpan playingDuration = default)
    {
        if (record is null) return new LyricsResult.Missing();
        var lines = LrcParser.Parse(record.SyncedLyrics);
        if (lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)))
        {
            // A radio edit or music video can have valid words but incompatible timestamps.
            if (playingDuration > TimeSpan.Zero && record.Duration > 0 &&
                Math.Abs(record.Duration - playingDuration.TotalSeconds) > DurationToleranceSeconds)
                return new LyricsResult.Plain(record.PlainLyrics ?? string.Join("\n", lines.Select(line => line.Text)));
            return new LyricsResult.Synced(lines);
        }
        if (record.Instrumental) return new LyricsResult.Instrumental();
        return string.IsNullOrWhiteSpace(record.PlainLyrics)
            ? new LyricsResult.Missing() : new LyricsResult.Plain(record.PlainLyrics.Trim());
    }
}
