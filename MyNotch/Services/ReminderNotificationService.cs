using Microsoft.UI.Dispatching;
using MyNotch.Interop;
using MyNotch.Models;

namespace MyNotch.Services;

public sealed class ReminderNotificationService : IDisposable
{
    private readonly NotificationHub _hub;
    private readonly CalendarTodoService _todos;
    private readonly DispatcherQueueTimer _todoTimer, _metricsTimer;
    private CpuCounters? _cpu;
    private ResourceAlertState _cpuAlert = new(), _ramAlert = new();
    private readonly HashSet<Guid> _deliveredThisRun = new();
    public ReminderNotificationService(DispatcherQueue dispatcher, NotificationHub hub, CalendarTodoService todos)
    {
        _hub = hub; _todos = todos;
        _todoTimer = dispatcher.CreateTimer(); _todoTimer.IsRepeating = false; _todoTimer.Tick += (_, _) => CheckTodos();
        // CPU/RAM counters require sampling. This reads only two cheap native counters every 5s,
        // independently of the heavier visible dashboard; device events never use this timer.
        _metricsTimer = dispatcher.CreateTimer(); _metricsTimer.IsRepeating = true; _metricsTimer.Interval = TimeSpan.FromSeconds(5); _metricsTimer.Tick += (_, _) => CheckResources();
        _todos.Changed += TodosChanged;
    }
    private void TodosChanged(object? sender, EventArgs e) => Schedule();
    public void ClockChanged()
    {
        _cpu = null; _cpuAlert = new(); _ramAlert = new();
        Schedule();
    }
    public void Schedule()
    {
        _todoTimer.Stop(); _metricsTimer.Stop();
        if (_hub.Enabled(NotificationKind.Resources)) _metricsTimer.Start();
        else { _cpu = null; _cpuAlert = new(); _ramAlert = new(); }
        if (!_hub.Enabled(NotificationKind.Todo)) return;
        DateTimeOffset now = DateTimeOffset.Now;
        var next = _todos.Items.Where(t => !_deliveredThisRun.Contains(t.Id)).Select(t => AlertThresholds.ReminderAt(t, TimeZoneInfo.Local)).Where(t => t is not null && t >= now.AddDays(-1)).Min();
        if (next is null) return;
        _todoTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp((next.Value - now).TotalMilliseconds, 100, TimeSpan.FromHours(1).TotalMilliseconds)); _todoTimer.Start();
    }
    private void CheckTodos()
    {
        var now = DateTimeOffset.Now;
        foreach (var task in _todos.Items.ToArray())
        {
            if (!_hub.Enabled(NotificationKind.Todo) || _deliveredThisRun.Contains(task.Id) || AlertThresholds.ReminderAt(task, TimeZoneInfo.Local) is not { } at || at > now || at < now.AddDays(-1)) continue;
            // Persist delivery before showing so a restart does not replay all reminders.
            if (_todos.Apply(new TodoInput.ReminderDelivered(task.Id, task.ReminderTime!.Value)))
                _hub.Post(new("todo:" + task.Id, NotificationKind.Todo, "Task reminder", task.Text, now));
            else { _deliveredThisRun.Add(task.Id); _hub.Post(new("todo:" + task.Id, NotificationKind.Todo, "Task reminder", task.Text + " (delivery could not be saved)", now)); }
        }
        Schedule();
    }
    private void CheckResources()
    {
        if (!_hub.Enabled(NotificationKind.Resources)) return;
        var now = DateTimeOffset.Now;
        double? cpu = null;
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        { var counters = new CpuCounters(idle.Ticks, kernel.Ticks, user.Ticks); cpu = SystemUsageMath.CpuPercent(_cpu, counters); _cpu = counters; }
        else _cpu = null;
        var memory = new NativeMethods.MemoryStatusEx { Length = 64 };
        double? ram = NativeMethods.GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 ? 100d * (memory.TotalPhysical - Math.Min(memory.TotalPhysical, memory.AvailablePhysical)) / memory.TotalPhysical : null;
        var cpuResult = AlertThresholds.Resource(_cpuAlert, cpu, now); _cpuAlert = cpuResult.State;
        var ramResult = AlertThresholds.Resource(_ramAlert, ram, now); _ramAlert = ramResult.State;
        if (cpuResult.Notify) _hub.Post(new("resource:cpu", NotificationKind.Resources, "High CPU usage", $"CPU at {cpu:0}% for 30 seconds", now));
        if (ramResult.Notify) _hub.Post(new("resource:ram", NotificationKind.Resources, "High memory usage", $"RAM at {ram:0}% for 30 seconds", now));
    }
    public void Dispose() { _todos.Changed -= TodosChanged; _todoTimer.Stop(); _metricsTimer.Stop(); }
}
