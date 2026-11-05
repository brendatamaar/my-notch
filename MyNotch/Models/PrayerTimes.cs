using System.Text.Json;

namespace MyNotch.Models;

/// <summary>One day of prayer times with next-prayer calculation (PLAN Phase 4, PRD §3.6).</summary>
public sealed record PrayerTimes
{
    public TimeOnly? Imsak { get; init; }
    public TimeOnly Fajr { get; init; }
    public TimeOnly Sunrise { get; init; }
    public TimeOnly Dhuhr { get; init; }
    public TimeOnly Asr { get; init; }
    public TimeOnly Maghrib { get; init; }
    public TimeOnly Isha { get; init; }

    /// <summary>Next upcoming prayer after <paramref name="now"/>; rolls to tomorrow's Fajr past Isha.</summary>
    public (string Name, DateTime At) GetNext(DateTime now)
    {
        var ordered = new (string Name, TimeOnly Time)[]
        {
            ("Fajr", Fajr), ("Dhuhr", Dhuhr),
            ("Asr", Asr), ("Maghrib", Maghrib), ("Isha", Isha),
        };
        foreach (var (name, time) in ordered)
        {
            var at = now.Date + time.ToTimeSpan();
            if (at > now) return (name, at);
        }
        return ("Fajr", now.Date.AddDays(1) + Fajr.ToTimeSpan());
    }

    /// <summary>Parses one Aladhan <c>timings/{date}</c> response body. Returns null when invalid.</summary>
    public static PrayerTimes? ParseDay(string json)
    {
        AladhanDayResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(json, PrayerTimesJsonContext.Default.AladhanDayResponse);
        }
        catch (JsonException)
        {
            return null;
        }

        var t = response?.Data?.Timings;
        if (t is null) return null;

        if (!TryParseTime(t.Fajr, out var fajr) || !TryParseTime(t.Sunrise, out var sunrise) ||
            !TryParseTime(t.Dhuhr, out var dhuhr) || !TryParseTime(t.Asr, out var asr) ||
            !TryParseTime(t.Maghrib, out var maghrib) || !TryParseTime(t.Isha, out var isha))
        {
            return null;
        }

        return new PrayerTimes
        {
            Imsak = TryParseTime(t.Imsak, out var imsak) ? imsak : null,
            Fajr = fajr, Sunrise = sunrise, Dhuhr = dhuhr,
            Asr = asr, Maghrib = maghrib, Isha = isha,
        };
    }

    // Timings arrive as "HH:mm" (a zone suffix like "HH:mm (EET)" is tolerated).
    internal static bool TryParseTime(string? s, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var head = s.Split(' ')[0];
        return TimeOnly.TryParseExact(head, "HH:mm", out time);
    }
}
