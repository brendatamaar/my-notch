using System.Globalization;
using System.Text.Json;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Provider wire formats stop here. Missing or invalid quotas never become zero usage.</summary>
public static class AgentUsageParser
{
    internal static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    internal static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static DateTimeOffset? Time(JsonElement value, bool milliseconds = false)
    {
        if (Text(value) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
        if (Number(value) is not { } unix) return null;
        try { return milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)unix)) : DateTimeOffset.FromUnixTimeSeconds(checked((long)unix)); }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
    }
    private static void Add(List<UsageWindow> windows, string label, double? used, DateTimeOffset? reset)
    {
        if (used is >= 0 and <= 100) windows.Add(new(label, used.Value, reset));
    }
    private static string Duration(double? minutes, string fallback) => minutes switch
    { 10080 => "Weekly", 1440 => "Daily", 300 => "5-hour", > 0 when minutes % 60 == 0 => $"{minutes / 60:0}-hour", > 0 => $"{minutes:0}-minute", _ => fallback };

    public static UsageOutcome Parse(UsageProvider provider, string json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var windows = new List<UsageWindow>();
            string? plan = null;
            int availableResets = 0;
            var resetCredits = new List<UsageResetCredit>();
            switch (provider)
            {
                case UsageProvider.Codex:
                    root = Get(root, "result").ValueKind == JsonValueKind.Object ? Get(root, "result") : root;
                    var rate = Get(Get(root, "rateLimitsByLimitId"), "codex");
                    if (rate.ValueKind != JsonValueKind.Object) rate = Get(root, "rateLimits");
                    plan = Text(Get(rate, "planType"));
                    var resets = Get(root, "rateLimitResetCredits");
                    if (Number(Get(resets, "availableCount")) is { } count && count >= 0 && count <= 10000) availableResets = (int)count;
                    var credits = Get(resets, "credits");
                    if (credits.ValueKind == JsonValueKind.Array)
                        foreach (var credit in credits.EnumerateArray())
                        {
                            string? id = Text(Get(credit, "id"));
                            var expires = Time(Get(credit, "expiresAt"));
                            if (!string.IsNullOrWhiteSpace(id) && id.Length <= 512 && Text(Get(credit, "status")) == "available" && (expires is null || expires > now))
                                resetCredits.Add(new(id, Text(Get(credit, "title")) ?? "Reset limit", expires));
                        }
                    foreach (var (key, fallback) in new[] { ("primary", "5-hour"), ("secondary", "Weekly") })
                    {
                        var row = Get(rate, key);
                        Add(windows, Duration(Number(Get(row, "windowDurationMins")), fallback), Number(Get(row, "usedPercent")), Time(Get(row, "resetsAt")));
                    }
                    break;
                case UsageProvider.Claude:
                    foreach (var (key, label) in new[] { ("five_hour", "5-hour"), ("seven_day", "Weekly"), ("seven_day_opus", "Weekly - Opus"), ("seven_day_sonnet", "Weekly - Sonnet") })
                    {
                        var row = Get(root, key);
                        Add(windows, label, Number(Get(row, "utilization")), Time(Get(row, "resets_at")));
                    }
                    var limits = Get(root, "limits");
                    if (limits.ValueKind == JsonValueKind.Array)
                        foreach (var row in limits.EnumerateArray())
                        {
                            var kind = Text(Get(row, "kind"));
                            var model = Text(Get(Get(Get(row, "scope"), "model"), "display_name"));
                            string? label = kind switch { "session" => "5-hour", "weekly_all" => "Weekly", "weekly_scoped" when model is not null => $"Weekly - {model}", _ => null };
                            if (label is not null && !windows.Any(w => w.Label == label)) Add(windows, label, Number(Get(row, "percent")), Time(Get(row, "resets_at")));
                        }
                    break;
                case UsageProvider.Antigravity:
                    var command = Get(root, "command");
                    if (Text(Get(root, "status")) != "SUCCESS" || Text(Get(command, "name")) != "usage" || Number(Get(root, "num_turns")) is > 0 || !string.IsNullOrEmpty(Text(Get(root, "conversation_id")))) return new(null, UsageProblem.InvalidData);
                    var groups = Get(Get(command, "data"), "groups");
                    if (groups.ValueKind == JsonValueKind.Array)
                        foreach (var group in groups.EnumerateArray())
                        {
                            string name = Text(Get(group, "name")) ?? "Model pool";
                            var buckets = Get(group, "buckets");
                            if (buckets.ValueKind != JsonValueKind.Array) continue;
                            foreach (var row in buckets.EnumerateArray())
                            {
                                if (Get(row, "disabled").ValueKind == JsonValueKind.True) continue;
                                if (Number(Get(row, "remaining_fraction")) is not { } left || left < 0 || left > 1) continue;
                                string label = Text(Get(row, "window")) switch { "weekly" => "Weekly", "5h" => "5-hour", _ => Text(Get(row, "name")) ?? "Limit" };
                                Add(windows, $"{name} / {label}", 100 * (1 - left), Time(Get(row, "reset_time")));
                            }
                        }
                    break;
                case UsageProvider.Muse:
                    var allowance = Get(root, "allowance");
                    if (allowance.ValueKind != JsonValueKind.Object) return new(null, Text(Get(root, "reason")) switch { "not_logged_in" => UsageProblem.SignInRequired, "server_sent_no_usage" or "no_allowance" => UsageProblem.NoData, _ => UsageProblem.InvalidData });
                    plan = Text(Get(allowance, "tier"));
                    now = Time(Get(allowance, "asOfMs"), true) ?? now;
                    foreach (var (key, label) in new[] { ("window", "Session"), ("weekly", "Weekly") })
                    {
                        var row = Get(allowance, key);
                        Add(windows, label, Number(Get(row, "usedPercent")), Time(Get(row, "resetsAtMs"), true));
                    }
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(provider));
            }
            return windows.Count == 0 ? new(null, UsageProblem.NoData) : new(new(provider, plan, now, windows, availableResets, resetCredits), null);
        }
        catch (JsonException) { return new(null, UsageProblem.InvalidData); }
    }
}
