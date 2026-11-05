using System.Text.Json.Serialization;

namespace MyNotch.Models;

/// <summary>User preferences persisted in %LOCALAPPDATA%\MyNotch\settings.json. Immutable; change via SettingsService.Update.</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;

    // General
    public bool HideInFullscreen { get; init; } = true;
    /// <summary>"primary", "follow" (monitor of the active window) or "display:&lt;DisplayId&gt;".</summary>
    public string Display { get; init; } = "primary";

    // Focus
    public int FocusMinutes { get; init; } = 25;
    public int ShortBreakMinutes { get; init; } = 5;
    public int LongBreakMinutes { get; init; } = 15;

    // Wellness (on/off lives in the notification toggles)
    public int StretchMinutes { get; init; } = 50;
    public int WaterMinutes { get; init; } = 45;

    // Agents
    public bool NotchApprovals { get; init; } = true;
    public int ApprovalWaitSeconds { get; init; } = 60;
    public string[] DisabledUsageProviders { get; init; } = [];

    // Developer
    public bool ClipboardHistory { get; init; } = true;
    public bool WatchCi { get; init; } = true;

    // Calendar
    public string CalendarFeedUrl { get; init; } = "";
    public int MeetingLeadMinutes { get; init; } = 10;

    public AppSettings Normalize() => this with
    {
        Display = string.IsNullOrWhiteSpace(Display) ? "primary" : Display.Trim(),
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180),
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60),
        LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 120),
        StretchMinutes = Math.Clamp(StretchMinutes, 10, 180),
        WaterMinutes = Math.Clamp(WaterMinutes, 10, 180),
        ApprovalWaitSeconds = Math.Clamp(ApprovalWaitSeconds, 10, 540),
        DisabledUsageProviders = DisabledUsageProviders ?? [],
        CalendarFeedUrl = (CalendarFeedUrl ?? "").Trim(),
        MeetingLeadMinutes = Math.Clamp(MeetingLeadMinutes, 1, 60),
    };

    public FocusConfig FocusConfig => new(TimeSpan.FromMinutes(FocusMinutes), TimeSpan.FromMinutes(ShortBreakMinutes), TimeSpan.FromMinutes(LongBreakMinutes));

    // Records compare arrays by reference; compare the provider list by content so no-op updates skip the disk write.
    public bool SameAs(AppSettings other) =>
        this with { DisabledUsageProviders = [] } == other with { DisabledUsageProviders = [] }
        && DisabledUsageProviders.Order().SequenceEqual(other.DisabledUsageProviders.Order());
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
