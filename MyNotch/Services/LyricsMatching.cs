using System.Text.RegularExpressions;
using MyNotch.Models;
namespace MyNotch.Services;

internal static class LyricsMatching
{
    public static string CleanTitle(string title)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var clean = Regex.Replace(title,
            @"\s*[\[(](?:official\s+)?(?:music\s+video|video|audio|lyrics?|lyric\s+video|visuali[sz]er)[\])]", string.Empty, options);
        clean = Regex.Replace(clean, @"\s*[\[(](?:feat\.?|ft\.?|with)\s+[^)\]]*[)\]]", string.Empty, options);
        return Regex.Replace(clean, @"\s+(?:feat\.?|ft\.?)\s+.*$", string.Empty, options).Trim();
    }
    public static string PrimaryArtist(string artist) => artist.Split(new[] { ", ", "; ", " & " }, StringSplitOptions.None)[0].Trim();
    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    public static LrclibResponse? Select(IEnumerable<LrclibResponse> records, LyricsQuery query) => records
        .Where(record => Key(CleanTitle(record.TrackName)) == Key(CleanTitle(query.Title)))
        .Where(record => !string.IsNullOrWhiteSpace(query.Artist) && Key(PrimaryArtist(record.ArtistName)) == Key(PrimaryArtist(query.Artist)))
        .Where(record => query.Duration <= TimeSpan.Zero ||
            (double.IsFinite(record.Duration) && Math.Abs(record.Duration - query.Duration.TotalSeconds) <= 6))
        .OrderByDescending(record => LrcParser.Parse(record.SyncedLyrics).Any(line => !string.IsNullOrWhiteSpace(line.Text)))
        .ThenByDescending(record => !string.IsNullOrWhiteSpace(query.Album) && Key(record.AlbumName ?? "") == Key(query.Album))
        .ThenBy(record => Math.Abs(record.Duration - query.Duration.TotalSeconds)).FirstOrDefault();
    public static LyricsResult Decode(LrclibResponse? record, TimeSpan playingDuration = default)
    {
        if (record is null) return new LyricsResult.Missing();
        var lines = LrcParser.Parse(record.SyncedLyrics);
        if (lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)))
        {
            // A radio edit or music video can have valid words but incompatible timestamps.
            if (playingDuration > TimeSpan.Zero && record.Duration > 0 &&
                Math.Abs(record.Duration - playingDuration.TotalSeconds) > 6)
                return new LyricsResult.Plain(record.PlainLyrics ?? string.Join("\n", lines.Select(line => line.Text)));
            return new LyricsResult.Synced(lines);
        }
        if (record.Instrumental) return new LyricsResult.Instrumental();
        return string.IsNullOrWhiteSpace(record.PlainLyrics)
            ? new LyricsResult.Missing() : new LyricsResult.Plain(record.PlainLyrics.Trim());
    }
}
