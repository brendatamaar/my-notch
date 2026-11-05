namespace MyNotch.Models;

// Append only: preferences persist these values.
public enum NotificationKind { General, Todo, AiQuota, Resources, WindowsApp, Bluetooth, Volume, Brightness, Meeting, Ci, Stretch, Water }
public sealed record NotchNotification(string Key, NotificationKind Kind, string Title, string Message, DateTimeOffset At, int? Level = null, TimeSpan? VisibleFor = null)
{
    public bool IsHud => Kind is NotificationKind.Volume or NotificationKind.Brightness;
    public TimeSpan Duration => VisibleFor ?? (IsHud ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(8));
    public string Glyph => Kind switch
    {
        NotificationKind.Volume => Level == 0 ? "\uE74F" : "\uE767",
        NotificationKind.Brightness => "\uE706", NotificationKind.Bluetooth => "\uE702",
        NotificationKind.Todo => "\uE73E", NotificationKind.AiQuota => "\uE734",
        NotificationKind.Resources => "\uE9D9", NotificationKind.Meeting => "\uE8F2", NotificationKind.Ci => "\uE9F5",
        NotificationKind.Stretch => "\uE805", // Walk
        NotificationKind.Water => "\uEB42",   // Drop
        _ => "\uE7F4",
    };
}
public sealed record NotificationQueue(NotchNotification? Current, DateTimeOffset Until, IReadOnlyList<NotchNotification> Pending)
{
    public static NotificationQueue Empty => new(null, DateTimeOffset.MinValue, Array.Empty<NotchNotification>());
}
public abstract record NotificationInput
{
    public sealed record Receive(NotchNotification Item) : NotificationInput;
    public sealed record Advance(bool Dismiss = false) : NotificationInput;
}
public static class NotificationRules
{
    public static NotificationQueue Step(NotificationQueue state, NotificationInput input, DateTimeOffset now)
    {
        if (input is NotificationInput.Receive { Item: var item })
        {
            // HUD changes replace their previous value, rather than replaying intermediate key presses.
            if (state.Current is null || state.Until <= now && state.Pending.Count == 0 || state.Current.Key == item.Key)
                return new(item, now + item.Duration, state.Pending.Where(p => p.Key != item.Key).ToArray());
            return state with { Pending = state.Pending.Where(p => p.Key != item.Key).Append(item).TakeLast(16).ToArray() };
        }
        if (input is NotificationInput.Advance advance && (advance.Dismiss || state.Until <= now))
        {
            var next = state.Pending.FirstOrDefault(p => now - p.At < TimeSpan.FromMinutes(2));
            return next is null ? NotificationQueue.Empty : new(next, now + next.Duration, state.Pending.SkipWhile(p => p != next).Skip(1).ToArray());
        }
        return state;
    }
}
public sealed record ResourceAlertState(DateTimeOffset? HighSince = null, bool Warned = false);
public static class AlertThresholds
{
    public static (int Band, bool Notify) Quota(int previous, double percent)
    {
        if (!double.IsFinite(percent) || percent < 0 || percent > 100) return (previous, false);
        if (percent < 80) return (0, false);
        int band = percent >= 100 ? 2 : percent >= 90 ? 1 : 0;
        return (Math.Max(previous, band), band > previous);
    }
    public static (ResourceAlertState State, bool Notify) Resource(ResourceAlertState state, double? percent, DateTimeOffset now)
    {
        if (percent is null || !double.IsFinite(percent.Value) || percent < 80) return (new(), false);
        if (state.Warned) return (state, false);
        if (percent < 90) return (state with { HighSince = null }, false);
        var since = state.HighSince ?? now;
        bool notify = now - since >= TimeSpan.FromSeconds(30);
        return (new(since, notify), notify);
    }
    public static DateTimeOffset? ReminderAt(CalendarTask task, TimeZoneInfo zone)
    {
        if (task.Completed || task.ReminderSent || task.ReminderTime is not { } time) return null;
        var local = task.Date.ToDateTime(time, DateTimeKind.Unspecified);
        // A clock time skipped by daylight-saving time is moved to the next valid minute.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
