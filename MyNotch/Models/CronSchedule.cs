using System.Globalization;
using System.Text;

namespace MyNotch.Models;

/// <summary>
/// Standard 5-field cron (minute hour day-of-month month day-of-week) with lists, ranges, steps,
/// month/day names and @macros. Day-of-month and day-of-week match with OR when both are restricted (Vixie cron).
/// Pure and allocation-light; used to explain copied cron expressions.
/// </summary>
public sealed class CronSchedule
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    private static readonly string[] DayLabels = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] MonthLabels = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private readonly ulong _minutes, _hours, _days, _months, _weekdays;
    private readonly bool _dayRestricted, _weekdayRestricted;
    public string Expression { get; }
    public string[] Fields { get; }

    private CronSchedule(string expression, string[] fields, ulong minutes, ulong hours, ulong days, ulong months, ulong weekdays)
    {
        Expression = expression; Fields = fields;
        _minutes = minutes; _hours = hours; _days = days; _months = months; _weekdays = weekdays;
        _dayRestricted = fields[2] is not ("*" or "?");
        _weekdayRestricted = fields[4] is not ("*" or "?");
    }

    public static bool TryParse(string text, out CronSchedule? schedule)
    {
        schedule = null;
        var expression = text.Trim();
        string normalized = expression.ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            _ => expression,
        };
        var fields = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;
        if (!TryField(fields[0], 0, 59, null, out var minutes) || !TryField(fields[1], 0, 23, null, out var hours)
            || !TryField(fields[2], 1, 31, null, out var days) || !TryField(fields[3], 1, 12, MonthNames, out var months)
            || !TryField(fields[4], 0, 7, DayNames, out var weekdays)) return false;
        if ((weekdays & (1UL << 7)) != 0) weekdays = (weekdays | 1) & ~(1UL << 7); // 7 is also Sunday
        schedule = new CronSchedule(expression, fields, minutes, hours, days, months, weekdays);
        return true;
    }

    private static bool TryField(string field, int min, int max, string[]? names, out ulong bits)
    {
        bits = 0;
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0) return false;
            string range = part; int step = 1;
            int slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0) return false;
            }
            int from, to;
            if (range is "*" or "?") { from = min; to = max; }
            else
            {
                int dash = range.IndexOf('-');
                if (dash > 0)
                {
                    if (!TryValue(range[..dash], names, min, out from) || !TryValue(range[(dash + 1)..], names, min, out to)) return false;
                }
                else
                {
                    if (!TryValue(range, names, min, out from)) return false;
                    to = slash >= 0 ? max : from; // "5/15" means 5, 20, 35, 50
                }
            }
            if (from < min || to > max || from > to) return false;
            for (int v = from; v <= to; v += step) bits |= 1UL << v;
        }
        return bits != 0;
    }

    private static bool TryValue(string text, string[]? names, int min, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
        if (names is not null)
        {
            int index = Array.IndexOf(names, text.ToUpperInvariant());
            if (index >= 0) { value = index + (min == 1 ? 1 : 0); return true; }
        }
        return false;
    }

    private bool DayMatches(DateTime d)
    {
        bool dom = (_days & (1UL << d.Day)) != 0, dow = (_weekdays & (1UL << (int)d.DayOfWeek)) != 0;
        return _dayRestricted && _weekdayRestricted ? dom || dow : dom && dow;
    }

    /// <summary>The next run strictly after <paramref name="after"/> (local wall-clock), within ~5 years; null if impossible (e.g. Feb 30).</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var limit = after.AddYears(5);
        while (t <= limit)
        {
            if ((_months & (1UL << t.Month)) == 0) { t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind).AddMonths(1); continue; }
            if (!DayMatches(t)) { t = t.Date.AddDays(1); continue; }
            if ((_hours & (1UL << t.Hour)) == 0) { t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind).AddHours(1); continue; }
            if ((_minutes & (1UL << t.Minute)) == 0) { t = t.AddMinutes(1); continue; }
            return t;
        }
        return null;
    }

    public IReadOnlyList<DateTime> NextRuns(DateTime after, int count)
    {
        var list = new List<DateTime>(count);
        var from = after;
        while (list.Count < count && Next(from) is { } next) { list.Add(next); from = next; }
        return list;
    }

    /// <summary>Plain-English summary, e.g. "At 09:30 on Monday through Friday".</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        string m = Fields[0], h = Fields[1];
        bool singleMinute = int.TryParse(m, out int minute), singleHour = int.TryParse(h, out int hour);
        if (singleMinute && singleHour) sb.Append($"At {hour:00}:{minute:00}");
        else if (m == "*" && h == "*") sb.Append("Every minute");
        else if (m.StartsWith("*/", StringComparison.Ordinal) && h == "*") sb.Append($"Every {m[2..]} minutes");
        else if (m.StartsWith("*/", StringComparison.Ordinal)) sb.Append($"Every {m[2..]} minutes during hour(s) {h}");
        else if (singleMinute && h == "*") sb.Append($"At minute {minute} of every hour");
        else if (singleMinute && h.StartsWith("*/", StringComparison.Ordinal)) sb.Append($"At minute {minute} every {h[2..]} hours");
        else if (m == "*") sb.Append($"Every minute during hour(s) {h}");
        else sb.Append($"At minute(s) {m} past hour(s) {(h == "*" ? "every hour" : h)}");

        if (Fields[2] is not ("*" or "?")) sb.Append($", on day {Fields[2]} of the month");
        if (Fields[4] is not ("*" or "?")) sb.Append(Fields[2] is not ("*" or "?") ? " or on " : " on ").Append(Names(Fields[4], DayNames, DayLabels, 0));
        if (Fields[3] is not ("*" or "?")) sb.Append(", in ").Append(Names(Fields[3], MonthNames, MonthLabels, 1));
        return sb.ToString();
    }

    private static string Names(string field, string[] names, string[] labels, int offset)
    {
        string Label(string token)
        {
            if (int.TryParse(token, out int n)) { n = labels.Length == 7 ? n % 7 : n - offset; return n >= 0 && n < labels.Length ? labels[n] : token; }
            int i = Array.IndexOf(names, token.ToUpperInvariant());
            return i >= 0 ? labels[i] : token;
        }
        return string.Join(", ", field.Split(',').Select(part =>
        {
            if (part.Contains('/')) return part;
            int dash = part.IndexOf('-');
            return dash > 0 ? $"{Label(part[..dash])} through {Label(part[(dash + 1)..])}" : Label(part);
        }));
    }
}
