using System.Globalization;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// Pure LRC parsing and lookup. Handles "[mm:ss.xx] text", several stamps on one line
/// ("[00:10.00][01:20.00] chorus") and ignores metadata tags like "[ar:Artist]".
/// </summary>
public static class LrcParser
{
    public static IReadOnlyList<LrcLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LrcLine>();

        var lines = new List<LrcLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            var stamps = new List<TimeSpan>();
            int pos = 0;
            while (pos < line.Length && line[pos] == '[')
            {
                int close = line.IndexOf(']', pos);
                if (close < 0) break;
                if (!TryParseStamp(line.AsSpan(pos + 1, close - pos - 1), out var t)) break;
                stamps.Add(t);
                pos = close + 1;
            }
            if (stamps.Count == 0) continue;

            var text = line[pos..].Trim();
            foreach (var t in stamps) lines.Add(new LrcLine(t, text));
        }

        lines.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public static int IndexAt(IReadOnlyList<LrcLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static bool TryParseStamp(ReadOnlySpan<char> s, out TimeSpan time)
    {
        time = default;
        int colon = s.IndexOf(':');
        if (colon <= 0) return false;
        if (!int.TryParse(s[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return false;
        if (!double.TryParse(s[(colon + 1)..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            return false;
        if (minutes < 0 || !double.IsFinite(seconds) || seconds < 0 || seconds >= 60) return false;
        time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}
