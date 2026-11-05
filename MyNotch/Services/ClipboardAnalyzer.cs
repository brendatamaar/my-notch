using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// Pure, AOT-safe clipboard text classifier (PLAN Phase 3, PRD §3.4). Order matters: the most specific shapes win.
/// JWT, JSON, cron, UUID, hex/rgb color, Unix timestamp, URL / percent-encoding, Base64 text.
/// No reflection, no dynamic codegen; regexes are source-generated.
/// </summary>
public static partial class ClipboardAnalyzer
{
    internal const int MaxPreviewChars = 200;
    internal const int MaxInputChars = 32 * 1024;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string FormatJson(JsonDocument doc)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        doc.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static ClipboardDetection Analyze(string? text) => Analyze(text, DateTimeOffset.Now);

    public static ClipboardDetection Analyze(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ClipboardDetection.None;

        var trimmed = text.Trim().Trim('"', '\'');
        if (trimmed.Length == 0 || trimmed.Length > MaxInputChars)
            return ClipboardDetection.None;

        return TryJwt(trimmed, now) ?? TryJson(trimmed) ?? TryCron(trimmed, now) ?? TryUuid(trimmed) ?? TryColor(trimmed)
            ?? TryTimestamp(trimmed, now) ?? TryUrl(trimmed) ?? TryBase64(trimmed) ?? ClipboardDetection.None;
    }

    // ------------------------------------------------------------------ JWT / JSON

    private static ClipboardDetection? TryJwt(string trimmed, DateTimeOffset now)
    {
        // Fast pre-check before splitting: JWTs start with the Base64Url header {"typ":"JWT"...
        if (!trimmed.StartsWith("eyJ", StringComparison.Ordinal))
            return null;

        var parts = trimmed.Split('.');
        if (parts.Length != 3 || !JwtChars().IsMatch(trimmed))
            return null;

        string payloadJson;
        try { payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1])); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }

        string formatted;
        string detail = "JWT payload (signature not verified)";
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            formatted = FormatJson(doc);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long seconds)
                && seconds is > 0 and < 253402300799)
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
                detail = at <= now ? $"Expired {Relative(at, now)} · signature not verified" : $"Expires {Relative(at, now)} ({at.ToLocalTime():dd MMM HH:mm}) · signature not verified";
            }
        }
        catch (JsonException) { return null; }

        return new ClipboardDetection { Kind = ClipboardKind.Jwt, Preview = Truncate(trimmed), Formatted = formatted, Detail = detail };
    }

    private static ClipboardDetection? TryJson(string trimmed)
    {
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return new ClipboardDetection { Kind = ClipboardKind.Json, Preview = Truncate(trimmed), Formatted = FormatJson(doc), Detail = "Valid JSON" };
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ cron

    private static ClipboardDetection? TryCron(string trimmed, DateTimeOffset now)
    {
        if (trimmed.Length > 120 || trimmed.Contains('\n')) return null;
        // A plain list of five numbers is far more often data than a schedule; require cron syntax.
        bool macro = trimmed.StartsWith('@');
        if (!macro && (!CronShape().IsMatch(trimmed) || trimmed.IndexOfAny(['*', '/']) < 0)) return null;
        if (!CronSchedule.TryParse(trimmed, out var cron) || cron is null) return null;
        var runs = cron.NextRuns(now.LocalDateTime, 5);
        var sb = new StringBuilder(cron.Describe()).Append('\n').Append('\n').Append("Next runs (local time):");
        foreach (var run in runs) sb.Append('\n').Append(run.ToString("ddd dd MMM yyyy  HH:mm", Invariant));
        if (runs.Count == 0) sb.Append("\nnever (no matching date)");
        return new ClipboardDetection { Kind = ClipboardKind.Cron, Preview = trimmed, Formatted = sb.ToString(), Detail = cron.Describe() };
    }

    // ------------------------------------------------------------------ UUID

    private static ClipboardDetection? TryUuid(string trimmed)
    {
        var match = UuidShape().Match(trimmed);
        if (!match.Success) return null;
        string hex = match.Groups["hex"].Value.Replace("-", "").ToLowerInvariant();
        string canonical = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        int version = Convert.ToInt32(hex[12].ToString(), 16);
        int variantNibble = Convert.ToInt32(hex[16].ToString(), 16);
        string variant = (variantNibble & 0x8) == 0 ? "NCS" : (variantNibble & 0xC) == 0x8 ? "RFC 9562" : (variantNibble & 0xE) == 0xC ? "Microsoft" : "future";
        string kind = hex == new string('0', 32) ? "Nil UUID" : hex == new string('f', 32) ? "Max UUID" : $"UUID v{version} · {variant} variant";

        var sb = new StringBuilder()
            .Append(canonical).Append('\n')
            .Append(canonical.ToUpperInvariant()).Append('\n')
            .Append('{').Append(canonical).Append('}').Append('\n')
            .Append(hex);
        DateTimeOffset? created = null;
        if (version == 7) created = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(hex[..12], 16));
        else if (version == 1)
        {
            long ticks = (Convert.ToInt64(hex[13..16], 16) << 48) | (Convert.ToInt64(hex[8..12], 16) << 32) | Convert.ToInt64(hex[..8], 16);
            created = new DateTimeOffset(1582, 10, 15, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks);
        }
        if (created is { } at) { kind += $" · created {at.ToLocalTime():dd MMM yyyy HH:mm:ss}"; sb.Append("\n\nCreated ").Append(at.ToString("O", Invariant)); }
        return new ClipboardDetection { Kind = ClipboardKind.Uuid, Preview = trimmed, Formatted = sb.ToString(), Detail = kind };
    }

    // ------------------------------------------------------------------ color

    private static ClipboardDetection? TryColor(string trimmed)
    {
        byte r, g, b, a = 255;
        var hex = HexColor().Match(trimmed);
        if (hex.Success)
        {
            string h = hex.Groups[1].Value;
            if (h.Length is 3 or 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            r = Convert.ToByte(h[..2], 16); g = Convert.ToByte(h[2..4], 16); b = Convert.ToByte(h[4..6], 16);
            if (h.Length == 8) a = Convert.ToByte(h[6..8], 16);
        }
        else
        {
            var rgb = RgbColor().Match(trimmed);
            if (!rgb.Success) return null;
            if (!byte.TryParse(rgb.Groups[1].Value, Invariant, out r) || !byte.TryParse(rgb.Groups[2].Value, Invariant, out g) || !byte.TryParse(rgb.Groups[3].Value, Invariant, out b)) return null;
            if (rgb.Groups[4].Success)
            {
                string alpha = rgb.Groups[4].Value;
                double value = alpha.EndsWith('%') ? double.Parse(alpha.TrimEnd('%'), Invariant) / 100 : double.Parse(alpha, Invariant);
                a = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
            }
        }
        var (hue, saturation, lightness) = Hsl(r, g, b);
        string hexText = a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        string rgbText = a == 255 ? $"rgb({r}, {g}, {b})" : $"rgba({r}, {g}, {b}, {(a / 255.0).ToString("0.##", Invariant)})";
        string hslText = $"hsl({hue:0}, {saturation:0}%, {lightness:0}%)";
        return new ClipboardDetection
        {
            Kind = ClipboardKind.Color, Preview = trimmed, ColorArgb = (uint)(a << 24 | r << 16 | g << 8 | b),
            Formatted = $"{hexText}\n{rgbText}\n{hslText}\nWinUI: #{a:X2}{r:X2}{g:X2}{b:X2}",
            Detail = $"{hexText} · {rgbText} · {hslText}",
        };
    }

    private static (double H, double S, double L) Hsl(byte red, byte green, byte blue)
    {
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l * 100);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, s * 100, l * 100);
    }

    // ------------------------------------------------------------------ Unix timestamp

    // 2000-01-01 .. 2100-01-01: wide enough for real timestamps, narrow enough to skip most ordinary numbers.
    private const long MinUnixSeconds = 946_684_800, MaxUnixSeconds = 4_102_444_800;

    private static ClipboardDetection? TryTimestamp(string trimmed, DateTimeOffset now)
    {
        if (trimmed.Length is not (10 or 13) || !trimmed.All(char.IsAsciiDigit) || trimmed[0] == '0') return null;
        long value = long.Parse(trimmed, Invariant);
        bool millis = trimmed.Length == 13;
        long seconds = millis ? value / 1000 : value;
        if (seconds is < MinUnixSeconds or > MaxUnixSeconds) return null;
        var at = millis ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
        var local = at.ToLocalTime();
        string formatted = $"{local:dddd, dd MMMM yyyy HH:mm:ss} (local, UTC{local:zzz})\n{at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\n{at.ToString(millis ? "yyyy-MM-ddTHH:mm:ss.fffZ" : "yyyy-MM-ddTHH:mm:ssZ", Invariant)}\n{Relative(at, now)}";
        return new ClipboardDetection
        {
            Kind = ClipboardKind.Timestamp, Preview = trimmed, Formatted = formatted,
            Detail = $"Unix {(millis ? "milliseconds" : "seconds")} · {local:dd MMM yyyy HH:mm} · {Relative(at, now)}",
        };
    }

    // ------------------------------------------------------------------ URL / percent-encoding

    private static ClipboardDetection? TryUrl(string trimmed)
    {
        if (trimmed.Contains('\n') || trimmed.Length > 8192) return null;
        bool encoded = PercentEscape().IsMatch(trimmed);
        bool isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var parameters = isUrl && uri!.Query.Length > 1 ? uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries) : [];
        // Plain links aren't interesting; links with a real query string or encoded text are.
        if (!encoded && parameters.Length < 2) return null;
        string decoded;
        try { decoded = Uri.UnescapeDataString(trimmed.Replace('+', ' ')); }
        catch (UriFormatException) { return null; }
        if (!isUrl && decoded == trimmed) return null;

        var sb = new StringBuilder(decoded);
        if (parameters.Length > 0)
        {
            sb.Append("\n\n").Append(uri!.GetLeftPart(UriPartial.Path)).Append('\n');
            foreach (var pair in parameters.Take(40))
            {
                int eq = pair.IndexOf('=');
                string key = Unescape(eq < 0 ? pair : pair[..eq]), value = eq < 0 ? "" : Unescape(pair[(eq + 1)..]);
                sb.Append("  ").Append(key).Append(" = ").Append(value).Append('\n');
            }
        }
        return new ClipboardDetection
        {
            Kind = ClipboardKind.Url, Preview = Truncate(trimmed), Formatted = sb.ToString().TrimEnd(),
            Detail = parameters.Length > 0 ? $"{uri!.Host} · {parameters.Length} query parameter(s)" : "Percent-encoded text",
        };

        static string Unescape(string s) { try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch (UriFormatException) { return s; } }
    }

    // ------------------------------------------------------------------ Base64

    private static ClipboardDetection? TryBase64(string trimmed)
    {
        if (trimmed.Length < 16 || trimmed.Length > 24 * 1024 || !Base64Shape().IsMatch(trimmed)) return null;
        // Words and identifiers often fit the alphabet; require something that looks encoded.
        if (!trimmed.Any(char.IsAsciiDigit) && !trimmed.Any(c => c is '+' or '/' or '=' or '-' or '_')) return null;
        byte[] bytes;
        try { bytes = DecodeBase64Url(trimmed.TrimEnd('=')); }
        catch (FormatException) { return null; }
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (ArgumentException) { return null; }
        if (text.Length == 0 || text.Count(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) > 0 || text.Count(char.IsLetterOrDigit) < text.Length / 2) return null;

        string formatted = text;
        string detail = $"Base64 · {bytes.Length} bytes of UTF-8 text";
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
        {
            try { using var doc = JsonDocument.Parse(text); formatted = FormatJson(doc); detail = $"Base64 · JSON ({bytes.Length} bytes)"; }
            catch (JsonException) { }
        }
        return new ClipboardDetection { Kind = ClipboardKind.Base64, Preview = Truncate(trimmed), Formatted = formatted, Detail = detail };
    }

    // ------------------------------------------------------------------ helpers

    internal static byte[] DecodeBase64Url(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid Base64 length");
        }
        return Convert.FromBase64String(s);
    }

    internal static string Relative(DateTimeOffset at, DateTimeOffset now)
    {
        var span = at - now;
        bool future = span > TimeSpan.Zero;
        var a = span.Duration();
        string amount = a.TotalSeconds < 60 ? "less than a minute"
            : a.TotalMinutes < 60 ? Plural((int)a.TotalMinutes, "minute")
            : a.TotalHours < 48 ? Plural((int)a.TotalHours, "hour")
            : a.TotalDays < 60 ? Plural((int)a.TotalDays, "day")
            : a.TotalDays < 730 ? Plural((int)(a.TotalDays / 30.44), "month")
            : Plural((int)(a.TotalDays / 365.25), "year");
        return future ? "in " + amount : amount + " ago";
        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
    }

    private static string Truncate(string s) =>
        s.Length <= MaxPreviewChars ? s : s[..MaxPreviewChars] + "…";

    [GeneratedRegex(@"^[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]*\.[A-Za-z0-9\-_]*$")]
    private static partial Regex JwtChars();

    [GeneratedRegex(@"^\S+(\s+\S+){4}$")]
    private static partial Regex CronShape();

    [GeneratedRegex(@"^[{(]?(?<hex>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})[)}]?$")]
    private static partial Regex UuidShape();

    [GeneratedRegex(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([0-9.]+%?)\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColor();

    [GeneratedRegex(@"%[0-9A-Fa-f]{2}")]
    private static partial Regex PercentEscape();

    [GeneratedRegex(@"^(?:[A-Za-z0-9+/]+={0,2}|[A-Za-z0-9\-_]+)$")]
    private static partial Regex Base64Shape();
}
