using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyNotch.Models;

public sealed record IcsEvent(
    string Uid, string Summary, DateTimeOffset Start, DateTimeOffset End, bool AllDay,
    string Location, string Description, string Url, string? RRule,
    IReadOnlyList<DateTimeOffset> ExDates, DateTimeOffset? RecurrenceId, bool Cancelled, TimeZoneInfo Zone);

/// <summary>One concrete meeting instance.</summary>
public sealed record Meeting(string Title, DateTimeOffset Start, DateTimeOffset End, string Location, string JoinUrl)
{
    public bool HasJoin => JoinUrl.Length > 0;
}

/// <summary>
/// Minimal RFC 5545 reader for published calendar feeds (Google "secret address in iCal format", Outlook/Microsoft 365
/// "publish calendar" ICS). Handles line folding, TZID (Windows ids, common IANA ids, VTIMEZONE fallback), all-day
/// events, DURATION, RRULE (DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH),
/// EXDATE, RECURRENCE-ID overrides and cancelled instances. Pure: no IO.
/// </summary>
public static partial class IcsCalendar
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    // Counts from DTSTART, so a daily series started 20 years ago must still reach today.
    private const int MaxInstancesPerSeries = 50_000;

    // Common IANA ids for when ICU isn't loaded (InvariantGlobalization) and FindSystemTimeZoneById can't convert.
    private static readonly Dictionary<string, string> IanaToWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Jakarta"] = "SE Asia Standard Time", ["Asia/Pontianak"] = "SE Asia Standard Time", ["Asia/Bangkok"] = "SE Asia Standard Time",
        ["Asia/Ho_Chi_Minh"] = "SE Asia Standard Time", ["Asia/Makassar"] = "Singapore Standard Time", ["Asia/Singapore"] = "Singapore Standard Time",
        ["Asia/Kuala_Lumpur"] = "Singapore Standard Time", ["Asia/Manila"] = "Singapore Standard Time", ["Asia/Jayapura"] = "Tokyo Standard Time",
        ["Asia/Tokyo"] = "Tokyo Standard Time", ["Asia/Seoul"] = "Korea Standard Time", ["Asia/Shanghai"] = "China Standard Time",
        ["Asia/Hong_Kong"] = "China Standard Time", ["Asia/Taipei"] = "Taipei Standard Time", ["Asia/Kolkata"] = "India Standard Time",
        ["Asia/Calcutta"] = "India Standard Time", ["Asia/Dubai"] = "Arabian Standard Time", ["Asia/Riyadh"] = "Arab Standard Time",
        ["Australia/Sydney"] = "AUS Eastern Standard Time", ["Australia/Melbourne"] = "AUS Eastern Standard Time", ["Australia/Perth"] = "W. Australia Standard Time",
        ["Pacific/Auckland"] = "New Zealand Standard Time", ["Europe/London"] = "GMT Standard Time", ["Europe/Dublin"] = "GMT Standard Time",
        ["Europe/Lisbon"] = "GMT Standard Time", ["Europe/Paris"] = "Romance Standard Time", ["Europe/Madrid"] = "Romance Standard Time",
        ["Europe/Brussels"] = "Romance Standard Time", ["Europe/Amsterdam"] = "W. Europe Standard Time", ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Rome"] = "W. Europe Standard Time", ["Europe/Stockholm"] = "W. Europe Standard Time", ["Europe/Zurich"] = "W. Europe Standard Time",
        ["Europe/Warsaw"] = "Central European Standard Time", ["Europe/Prague"] = "Central Europe Standard Time", ["Europe/Athens"] = "GTB Standard Time",
        ["Europe/Helsinki"] = "FLE Standard Time", ["Europe/Kiev"] = "FLE Standard Time", ["Europe/Kyiv"] = "FLE Standard Time", ["Europe/Istanbul"] = "Turkey Standard Time",
        ["Europe/Moscow"] = "Russian Standard Time", ["America/New_York"] = "Eastern Standard Time", ["America/Toronto"] = "Eastern Standard Time",
        ["America/Chicago"] = "Central Standard Time", ["America/Denver"] = "Mountain Standard Time", ["America/Phoenix"] = "US Mountain Standard Time",
        ["America/Los_Angeles"] = "Pacific Standard Time", ["America/Vancouver"] = "Pacific Standard Time", ["America/Anchorage"] = "Alaskan Standard Time",
        ["Pacific/Honolulu"] = "Hawaiian Standard Time", ["America/Sao_Paulo"] = "E. South America Standard Time", ["America/Mexico_City"] = "Central Standard Time (Mexico)",
        ["America/Bogota"] = "SA Pacific Standard Time", ["Africa/Johannesburg"] = "South Africa Standard Time", ["Africa/Cairo"] = "Egypt Standard Time",
        ["Africa/Lagos"] = "W. Central Africa Standard Time", ["Africa/Nairobi"] = "E. Africa Standard Time", ["Etc/UTC"] = "UTC", ["UTC"] = "UTC", ["GMT"] = "UTC",
    };

    private sealed record Property(string Name, Dictionary<string, string> Parameters, string Value);

    public static IReadOnlyList<IcsEvent> Parse(string text) => Parse(text, TimeZoneInfo.Local);

    public static IReadOnlyList<IcsEvent> Parse(string text, TimeZoneInfo local)
    {
        var lines = Unfold(text);
        var zoneOffsets = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var events = new List<IcsEvent>();
        List<Property>? current = null;
        string? zoneId = null; bool inStandard = false;

        foreach (var line in lines)
        {
            var p = ParseLine(line);
            if (p is null) continue;
            if (p.Name == "BEGIN")
            {
                switch (p.Value.ToUpperInvariant())
                {
                    case "VEVENT": current = new(); break;
                    case "VTIMEZONE": zoneId = null; break;
                    case "STANDARD": inStandard = true; break;
                }
                continue;
            }
            if (p.Name == "END")
            {
                switch (p.Value.ToUpperInvariant())
                {
                    case "VEVENT":
                        if (current is not null && Build(current, zoneOffsets, local) is { } e) events.Add(e);
                        current = null; break;
                    case "STANDARD": inStandard = false; break;
                }
                continue;
            }
            if (current is not null) { current.Add(p); continue; }
            if (p.Name == "TZID") zoneId = p.Value;
            else if (inStandard && zoneId is not null && p.Name == "TZOFFSETTO" && TryOffset(p.Value, out var offset)) zoneOffsets[zoneId] = offset;
        }
        return events;
    }

    private static IcsEvent? Build(List<Property> props, Dictionary<string, TimeSpan> zoneOffsets, TimeZoneInfo local)
    {
        Property? Get(string name) => props.FirstOrDefault(p => p.Name == name);
        var dtStart = Get("DTSTART");
        if (dtStart is null) return null;
        var zone = ZoneFor(dtStart, zoneOffsets, local);
        if (!TryDate(dtStart, zone, out var start, out bool allDay)) return null;

        DateTimeOffset end;
        if (Get("DTEND") is { } dtEnd && TryDate(dtEnd, ZoneFor(dtEnd, zoneOffsets, local), out var e, out _)) end = e;
        else if (Get("DURATION") is { } duration && TryDuration(duration.Value, out var span)) end = start + span;
        else end = allDay ? start.AddDays(1) : start;
        if (end < start) end = start;

        var exDates = new List<DateTimeOffset>();
        foreach (var ex in props.Where(p => p.Name == "EXDATE"))
        {
            var exZone = ZoneFor(ex, zoneOffsets, local);
            foreach (var value in ex.Value.Split(','))
                if (TryDate(ex with { Value = value.Trim() }, exZone, out var at, out _)) exDates.Add(at);
        }
        DateTimeOffset? recurrenceId = Get("RECURRENCE-ID") is { } rid && TryDate(rid, ZoneFor(rid, zoneOffsets, local), out var r, out _) ? r : null;

        return new IcsEvent(
            Get("UID")?.Value ?? Guid.NewGuid().ToString("N"), Unescape(Get("SUMMARY")?.Value ?? "(no title)"), start, end, allDay,
            Unescape(Get("LOCATION")?.Value ?? ""), Unescape(Get("DESCRIPTION")?.Value ?? ""), Get("URL")?.Value ?? "",
            Get("RRULE")?.Value, exDates, recurrenceId,
            string.Equals(Get("STATUS")?.Value, "CANCELLED", StringComparison.OrdinalIgnoreCase), zone);
    }

    /// <summary>Concrete timed or all-day instances overlapping [from, to), sorted by start.</summary>
    public static IReadOnlyList<Meeting> Occurrences(IReadOnlyList<IcsEvent> events, DateTimeOffset from, DateTimeOffset to, bool includeAllDay = false)
    {
        var overrides = events.Where(e => e.RecurrenceId is not null).ToLookup(e => e.Uid);
        var result = new List<Meeting>();
        void Add(IcsEvent e, DateTimeOffset start, DateTimeOffset end)
        {
            if (e.Cancelled || (e.AllDay && !includeAllDay) || end <= from || start >= to) return;
            result.Add(new Meeting(e.Summary, start, end, e.Location, JoinLink(e.Url, e.Location, e.Description)));
        }

        foreach (var e in events)
        {
            if (e.RecurrenceId is not null) { Add(e, e.Start, e.End); continue; }
            if (e.RRule is null) { Add(e, e.Start, e.End); continue; }
            var moved = overrides[e.Uid].Select(o => o.RecurrenceId!.Value.UtcTicks).ToHashSet();
            var excluded = e.ExDates.Select(d => d.UtcTicks).ToHashSet();
            var length = e.End - e.Start;
            foreach (var start in Expand(e, to))
            {
                if (excluded.Contains(start.UtcTicks) || moved.Contains(start.UtcTicks)) continue;
                Add(e, start, start + length);
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>RRULE instance starts (DTSTART first), up to <paramref name="until"/>.</summary>
    internal static IEnumerable<DateTimeOffset> Expand(IcsEvent e, DateTimeOffset until)
    {
        var rule = e.RRule!.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim().ToUpperInvariant(), kv => kv[1].Trim().ToUpperInvariant());
        string freq = rule.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), NumberStyles.None, Invariant, out int i) && i > 0 ? i : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), NumberStyles.None, Invariant, out int c) && c > 0 ? c : null;
        DateTimeOffset limit = until;
        if (rule.TryGetValue("UNTIL", out var untilText))
        {
            var untilProp = new Property("UNTIL", new(), untilText);
            if (TryDate(untilProp, e.Zone, out var u, out bool dateOnly))
            {
                if (dateOnly) u = u.AddDays(1).AddTicks(-1);
                if (u < limit) limit = u;
            }
        }
        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(ParseByDay).Where(d => d is not null).Select(d => d!.Value).ToArray() : [];
        var byMonthDay = rule.TryGetValue("BYMONTHDAY", out var bmd) ? bmd.Split(',').Select(s => int.TryParse(s, NumberStyles.AllowLeadingSign, Invariant, out int v) ? v : 0).Where(v => v != 0).ToArray() : [];
        var byMonth = rule.TryGetValue("BYMONTH", out var bm) ? bm.Split(',').Select(s => int.TryParse(s, out int v) ? v : 0).Where(v => v is >= 1 and <= 12).ToHashSet() : [];

        var localStart = TimeZoneInfo.ConvertTime(e.Start, e.Zone).DateTime;
        var time = localStart.TimeOfDay;
        int produced = 0;
        // Hard stop for the candidate loops: a rule that never matches (BYMONTHDAY=32, Feb 30) must not spin forever.
        var stop = TimeZoneInfo.ConvertTime(limit, e.Zone).DateTime.AddDays(2);
        if (stop > DateTime.MaxValue.AddYears(-2)) stop = DateTime.MaxValue.AddYears(-2);

        IEnumerable<DateTime> Candidates()
        {
            switch (freq)
            {
                case "DAILY":
                    for (var d = localStart.Date; d <= stop; d = d.AddDays(interval))
                        if ((byDay.Length == 0 || byDay.Any(b => b.Day == d.DayOfWeek)) && (byMonth.Count == 0 || byMonth.Contains(d.Month))) yield return d + time;
                    yield break;
                case "WEEKLY":
                {
                    var days = byDay.Length > 0 ? byDay.Select(b => b.Day).Distinct().ToArray() : [localStart.DayOfWeek];
                    var weekStart = localStart.Date.AddDays(-(((int)localStart.DayOfWeek + 6) % 7)); // WKST=MO
                    for (var w = weekStart; w <= stop; w = w.AddDays(7 * interval))
                        foreach (var day in days.OrderBy(d => ((int)d + 6) % 7))
                            yield return w.AddDays(((int)day + 6) % 7) + time;
                    yield break;
                }
                case "MONTHLY":
                    for (var m = new DateTime(localStart.Year, localStart.Month, 1); m <= stop; m = m.AddMonths(interval))
                        foreach (var d in MonthDays(m, byDay, byMonthDay.Length > 0 ? byMonthDay : byDay.Length > 0 ? [] : [localStart.Day]))
                            yield return d + time;
                    yield break;
                case "YEARLY":
                    for (int y = localStart.Year; y <= stop.Year; y += interval)
                    {
                        var months = byMonth.Count > 0 ? byMonth.Order().ToArray() : [localStart.Month];
                        foreach (var month in months)
                            foreach (var d in MonthDays(new DateTime(y, month, 1), byDay, byMonthDay.Length > 0 ? byMonthDay : byDay.Length > 0 ? [] : [localStart.Day]))
                                yield return d + time;
                    }
                    yield break;
                default:
                    yield return localStart;
                    yield break;
            }
        }

        foreach (var wall in Candidates())
        {
            if (wall < localStart) continue;
            var at = ToOffset(wall, e.Zone);
            if (at > limit || produced >= MaxInstancesPerSeries) yield break;
            if (count is { } n && produced >= n) yield break;
            produced++;
            yield return at;
            if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY")) yield break;
        }
    }

    private static IEnumerable<DateTime> MonthDays(DateTime month, (int Ordinal, DayOfWeek Day)[] byDay, int[] byMonthDay)
    {
        int length = DateTime.DaysInMonth(month.Year, month.Month);
        var days = new SortedSet<int>();
        foreach (int d in byMonthDay)
        {
            int day = d > 0 ? d : length + d + 1;
            if (day >= 1 && day <= length) days.Add(day);
        }
        foreach (var (ordinal, weekday) in byDay)
        {
            var matches = Enumerable.Range(1, length).Where(d => new DateTime(month.Year, month.Month, d).DayOfWeek == weekday).ToArray();
            if (ordinal == 0) foreach (int d in matches) days.Add(d);
            else
            {
                int index = ordinal > 0 ? ordinal - 1 : matches.Length + ordinal;
                if (index >= 0 && index < matches.Length) days.Add(matches[index]);
            }
        }
        foreach (int d in days) yield return new DateTime(month.Year, month.Month, d);
    }

    private static (int Ordinal, DayOfWeek Day)? ParseByDay(string text)
    {
        text = text.Trim();
        if (text.Length < 2) return null;
        string code = text[^2..];
        DayOfWeek? day = code switch { "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => null };
        if (day is null) return null;
        int ordinal = text.Length > 2 && int.TryParse(text[..^2], NumberStyles.AllowLeadingSign, Invariant, out int o) ? o : 0;
        return (ordinal, day.Value);
    }

    /// <summary>First video-call link found in the URL, location or description (Meet, Zoom, Teams, Webex).</summary>
    public static string JoinLink(params string[] fields)
    {
        foreach (var field in fields)
        {
            var match = JoinUrl().Match(field ?? "");
            if (match.Success) return match.Value.TrimEnd('.', ',', ')', '>', '"', '\'');
        }
        return "";
    }

    // ------------------------------------------------------------------ parsing helpers

    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { sb.Append(raw, 1, raw.Length - 1); continue; }
            if (sb.Length > 0) lines.Add(sb.ToString());
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        return lines;
    }

    private static Property? ParseLine(string line)
    {
        // The value starts at the first ':' outside a quoted parameter value.
        bool quoted = false; int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in head.Skip(1))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) parameters[part[..eq]] = part[(eq + 1)..].Trim('"');
        }
        return new Property(head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    private static TimeZoneInfo ZoneFor(Property p, Dictionary<string, TimeSpan> zoneOffsets, TimeZoneInfo local)
    {
        if (!p.Parameters.TryGetValue("TZID", out var id) || id.Length == 0) return local;
        id = id.TrimStart('/');
        if (TryFindZone(id) is { } zone) return zone;
        if (IanaToWindows.TryGetValue(id, out var windows) && TryFindZone(windows) is { } mapped) return mapped;
        if (zoneOffsets.TryGetValue(p.Parameters["TZID"], out var offset)) return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
        return local;

        static TimeZoneInfo? TryFindZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or System.Security.SecurityException) { return null; }
        }
    }

    private static bool TryDate(Property p, TimeZoneInfo zone, out DateTimeOffset value, out bool dateOnly)
    {
        value = default; dateOnly = false;
        string text = p.Value.Trim();
        if (text.Length == 8 && DateTime.TryParseExact(text, "yyyyMMdd", Invariant, DateTimeStyles.None, out var date))
        {
            dateOnly = true;
            value = ToOffset(date, zone);
            return true;
        }
        bool utc = text.EndsWith('Z');
        if (!DateTime.TryParseExact(text.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", Invariant, DateTimeStyles.None, out var dt)) return false;
        value = utc ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : ToOffset(dt, zone);
        return true;
    }

    private static DateTimeOffset ToOffset(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(30); // skipped by a DST jump
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private static bool TryOffset(string text, out TimeSpan offset)
    {
        offset = default;
        text = text.Trim();
        if (text.Length is not (5 or 7) || text[0] is not ('+' or '-')) return false;
        if (!int.TryParse(text[1..3], out int h) || !int.TryParse(text[3..5], out int m)) return false;
        offset = new TimeSpan(h, m, 0);
        if (text[0] == '-') offset = -offset;
        return true;
    }

    internal static bool TryDuration(string text, out TimeSpan span)
    {
        span = default;
        var m = Duration().Match(text.Trim());
        if (!m.Success) return false;
        int Part(int group) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, Invariant) : 0;
        span = new TimeSpan(Part(2) * 7 + Part(3), Part(4), Part(5), Part(6));
        if (m.Groups[1].Value == "-") span = -span;
        return true;
    }

    private static string Unescape(string text) =>
        text.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    [GeneratedRegex(@"^([+-]?)P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$")]
    private static partial Regex Duration();

    [GeneratedRegex(@"https://(?:meet\.google\.com/[a-z0-9-]+|[\w.-]*zoom\.us/(?:j|my|w)/[^\s<>""]+|teams\.microsoft\.com/l/meetup-join/[^\s<>""]+|teams\.live\.com/meet/[^\s<>""]+|[\w.-]*webex\.com/[^\s<>""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JoinUrl();
}
