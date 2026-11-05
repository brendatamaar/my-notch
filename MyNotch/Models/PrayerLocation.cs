namespace MyNotch.Models;

public sealed record PrayerLocation(string Id, string City, double Latitude, double Longitude, string Zone, string ApiZone, string ZoneLabel, string AreaType = "Kota")
{
    public string DisplayCity => Id == "jakarta" ? "DKI Jakarta" : $"{AreaType} {City}";
    public static readonly PrayerLocation[] Places =
    [
        new("jakarta", "Jakarta", -6.2088, 106.8456, "SE Asia Standard Time", "Asia/Jakarta", "WIB"),
        new("bogor-city", "Bogor", -6.5971, 106.8060, "SE Asia Standard Time", "Asia/Jakarta", "WIB"),
        new("bogor-regency", "Bogor", -6.4817, 106.8544, "SE Asia Standard Time", "Asia/Jakarta", "WIB", "Kabupaten"),
        new("bandung", "Bandung", -6.9175, 107.6191, "SE Asia Standard Time", "Asia/Jakarta", "WIB"),
        new("surabaya", "Surabaya", -7.2575, 112.7521, "SE Asia Standard Time", "Asia/Jakarta", "WIB"),
        new("yogyakarta", "Yogyakarta", -7.7956, 110.3695, "SE Asia Standard Time", "Asia/Jakarta", "WIB"),
        new("medan", "Medan", 3.5952, 98.6722, "SE Asia Standard Time", "Asia/Jakarta", "WIB"),
        new("denpasar", "Denpasar", -8.6500, 115.2167, "Singapore Standard Time", "Asia/Makassar", "WITA"),
        new("makassar", "Makassar", -5.1477, 119.4327, "Singapore Standard Time", "Asia/Makassar", "WITA"),
        new("jayapura", "Jayapura", -2.5337, 140.7181, "Tokyo Standard Time", "Asia/Jayapura", "WIT"),
    ];
    /// <summary>Initial great-circle bearing to the Kaaba, clockwise from true north.</summary>
    public double QiblaBearing
    {
        get
        {
            const double radians = Math.PI / 180;
            double latitude = Latitude * radians, kaabaLatitude = 21.4225 * radians;
            double longitudeDelta = (39.8262 - Longitude) * radians;
            double y = Math.Sin(longitudeDelta) * Math.Cos(kaabaLatitude);
            double x = Math.Cos(latitude) * Math.Sin(kaabaLatitude)
                - Math.Sin(latitude) * Math.Cos(kaabaLatitude) * Math.Cos(longitudeDelta);
            return (Math.Atan2(y, x) / radians + 360) % 360;
        }
    }
    public DateTime LocalNow(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(Zone)).DateTime;
    public DateTime ToMachineTime(DateTime cityTime) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(cityTime, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(Zone)).ToLocalTime();
}

public enum PrayerDayPeriod { Morning, Midday, Afternoon, Night }

public static class PrayerPresentation
{
    // Fixed 24-hour periods in the selected city's local time.
    public static PrayerDayPeriod DayPeriod(DateTime localNow) => localNow.Hour switch
    {
        >= 5 and < 11 => PrayerDayPeriod.Morning,
        >= 11 and < 15 => PrayerDayPeriod.Midday,
        >= 15 and < 18 => PrayerDayPeriod.Afternoon,
        _ => PrayerDayPeriod.Night,
    };
    public static string DisplayName(string? name) => name switch
    { "Fajr" => "Subuh", "Dhuhr" => "Dzuhur", "Asr" => "Ashar", "Maghrib" => "Maghrib", "Isha" => "Isya", _ => "Next prayer" };
    public static string Countdown(DateTime now, DateTime? next)
    {
        if (next is null) return "--:--:--";
        var seconds = Math.Max(0, (int)Math.Ceiling((next.Value - now).TotalSeconds));
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }
    // A timeline of the five prayers, not an astronomical sun elevation chart.
    public static double Progress(PrayerTimes day, DateTime now)
    {
        TimeOnly[] times = [day.Fajr, day.Dhuhr, day.Asr, day.Maghrib, day.Isha];
        double minute = now.TimeOfDay.TotalMinutes;
        if (minute <= times[0].ToTimeSpan().TotalMinutes) return 0;
        for (int i = 1; i < times.Length; i++)
        {
            double end = times[i].ToTimeSpan().TotalMinutes, start = times[i - 1].ToTimeSpan().TotalMinutes;
            if (minute < end) return (i - 1 + (minute - start) / Math.Max(1, end - start)) / 4;
        }
        return 1;
    }
    public static (double X, double Y) ArcPoint(double progress)
    {
        double t = Math.Clamp(progress, 0, 1), u = 1 - t;
        return (30 * u * u * u + 306 * u * u * t + 594 * u * t * t + 270 * t * t * t,
            64 * (u * u * u + t * t * t) - 12 * u * t);
    }
}
