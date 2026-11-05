using MyNotch.Models;
using MyNotch.Services;

internal static class NotificationTests
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        NotchNotification Notice(int id, NotificationKind kind = NotificationKind.Todo) => new(id.ToString(), kind, "Title", "Message", now);
        var state = NotificationRules.Step(NotificationQueue.Empty, new NotificationInput.Receive(Notice(1)), now);
        Check(state.Current?.Key == "1", "first notice appears");
        state = NotificationRules.Step(state, new NotificationInput.Receive(Notice(2)), now);
        Check(state.Current?.Key == "1" && state.Pending.Count == 1, "unrelated notice is queued");
        state = NotificationRules.Step(state, new NotificationInput.Advance(), now.AddSeconds(2));
        Check(state.Current?.Key == "1", "early timer cannot dismiss current notice");
        state = NotificationRules.Step(state, new NotificationInput.Advance(), now.AddSeconds(8));
        Check(state.Current?.Key == "2" && state.Until == now.AddSeconds(16), "queued notice gets its own display duration");
        state = NotificationRules.Step(state, new NotificationInput.Advance(true), now.AddSeconds(9));
        Check(state.Current is null && state.Pending.Count == 0, "dismiss drains queue");
        var hud = Notice(3, NotificationKind.Volume) with { Key = "volume", Level = 30 };
        state = NotificationRules.Step(NotificationQueue.Empty, new NotificationInput.Receive(hud), now);
        state = NotificationRules.Step(state, new NotificationInput.Receive(hud with { Level = 31, At = now.AddSeconds(2) }), now.AddSeconds(2));
        Check(state.Current?.Level == 31 && state.Pending.Count == 0 && state.Until == now.AddSeconds(5), "HUD changes replace and extend");
        for (int i = 0; i < 100; i++) state = NotificationRules.Step(state, new NotificationInput.Receive(Notice(i + 10)), now);
        Check(state.Pending.Count == 16, "pending queue bounded");
        state = NotificationRules.Step(state, new NotificationInput.Advance(), now.AddMinutes(3));
        Check(state.Current is null, "stale backlog expires");

        // Exhaustive short sequences collide on keys, timers and HUD updates.
        for (int sequence = 0; sequence < 4096; sequence++)
        {
            int digits = sequence; var queue = NotificationQueue.Empty;
            for (int step = 0; step < 6; step++, digits /= 4)
            {
                NotificationInput input = (digits % 4) switch { 0 => new NotificationInput.Receive(Notice(1)), 1 => new NotificationInput.Receive(hud), 2 => new NotificationInput.Advance(), _ => new NotificationInput.Advance(true) };
                queue = NotificationRules.Step(queue, input, now.AddSeconds(step * 2));
                Check(queue.Pending.Count <= 16 && queue.Pending.Select(n => n.Key).Distinct().Count() == queue.Pending.Count, "queue bounds and key uniqueness");
                Check(queue.Current is null || queue.Pending.All(n => n.Key != queue.Current.Key), "active notice not also queued");
            }
        }
        ResourceAlertState resources = new();
        for (int i = 0; i < 6; i++)
        {
            var result = AlertThresholds.Resource(resources, 95, now.AddSeconds(i * 5)); resources = result.State;
            Check(!result.Notify, "short load does not warn");
        }
        var sustained = AlertThresholds.Resource(resources, 95, now.AddSeconds(30)); resources = sustained.State;
        Check(sustained.Notify, "sustained load warns");
        Check(!AlertThresholds.Resource(resources, 100, now.AddHours(1)).Notify, "ongoing load does not repeat");
        resources = AlertThresholds.Resource(resources, 79, now.AddHours(1)).State;
        resources = AlertThresholds.Resource(resources, 90, now.AddHours(2)).State;
        Check(AlertThresholds.Resource(resources, 90, now.AddHours(2).AddSeconds(30)).Notify, "recovery rearms warning");
        Check(AlertThresholds.Resource(new(now), null, now.AddSeconds(30)).State.HighSince is null, "missing counter resets continuity");
        int band = 0;
        foreach (var item in new[] { (89d, false), (90d, true), (99d, false), (100d, true), (95d, false), (80d, false), (79d, false), (91d, true), (double.NaN, false) })
        { var result = AlertThresholds.Quota(band, item.Item1); band = result.Band; Check(result.Notify == item.Item2, "quota threshold/hysteresis"); }

        var task = new CalendarTask(Guid.NewGuid(), new(2026, 11, 4), "Reminder", false, new(19, 30));
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestJakarta", TimeSpan.FromHours(7), "TestJakarta", "TestJakarta");
        Check(AlertThresholds.ReminderAt(task, zone) == now.AddMinutes(30), "reminder honors timezone");
        Check(AlertThresholds.ReminderAt(task with { Completed = true }, zone) is null, "completed task excluded");
        Check(AlertThresholds.ReminderAt(task with { ReminderSent = true }, zone) is null, "delivered task excluded");
        Check(AlertThresholds.ReminderAt(task with { ReminderTime = null }, zone) is null, "no reminder by default");
        var changed = CalendarTodo.Step([task], new TodoInput.Remind(task.Id, new(20, 0))).Items;
        changed = CalendarTodo.Step(changed, new TodoInput.ReminderDelivered(task.Id, new(19, 30))).Items;
        Check(!changed[0].ReminderSent, "stale delivery cannot mark rescheduled task");
        string path = Path.Combine(Path.GetTempPath(), "my-notch-reminder-test-" + Guid.NewGuid().ToString("N"), "tasks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // Old v1 documents have no reminder fields.
            File.WriteAllText(path, "{\"version\":1,\"items\":[{\"id\":\"" + task.Id + "\",\"date\":\"2026-11-04\",\"text\":\"Legacy\",\"completed\":false}]}");
            var store = new CalendarTodoService(path);
            Check(store.Items.Count == 1 && store.Items[0].ReminderTime is null, "legacy v1 remains readable");
            int events = 0; store.Changed += (_, _) => events++;
            Check(store.Apply(new TodoInput.Remind(task.Id, new(19, 30))), "save reminder");
            Check(store.Apply(new TodoInput.ReminderDelivered(task.Id, new(19, 30))), "save delivery");
            var reloaded = new CalendarTodoService(path);
            Check(reloaded.Items[0].ReminderSent && reloaded.Items[0].ReminderTime == new TimeOnly(19, 30) && events == 2, "delivery survives restart and emits changes");
            Check(reloaded.Apply(new TodoInput.Remind(task.Id, new(20, 0))) && !reloaded.Items[0].ReminderSent, "rescheduling rearms task");
        }
        finally { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        Console.WriteLine("PASS: notifications (4096 queue sequences, HUD coalescing, resource/quota hysteresis, reminder persistence and legacy files)");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Notification invariant: " + message); }
}
