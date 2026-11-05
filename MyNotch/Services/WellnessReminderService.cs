using Microsoft.UI.Dispatching;
using MyNotch.Interop;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Samples idle time every 30 seconds and posts stretch / drink-water reminders decided by <see cref="WellnessRules"/>.</summary>
public sealed class WellnessReminderService : IDisposable
{
    private readonly NotificationHub _hub;
    private readonly Func<bool> _focusRunning;
    private readonly DispatcherQueueTimer _timer;
    private WellnessState _state = WellnessState.Initial;

    public WellnessReminderService(DispatcherQueue dispatcher, NotificationHub hub, Func<bool> focusRunning)
    {
        _hub = hub; _focusRunning = focusRunning;
        _timer = dispatcher.CreateTimer(); _timer.IsRepeating = true; _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() { _state = WellnessState.Initial; _timer.Start(); Tick(); }

    /// <summary>After sleep/resume or a clock change: start counting again from now.</summary>
    public void ClockChanged() => _state = _state with { LastTick = null };

    private void Tick()
    {
        var settings = SettingsService.Shared.Current;
        var config = WellnessConfig.Default with { Stretch = TimeSpan.FromMinutes(settings.StretchMinutes), Water = TimeSpan.FromMinutes(settings.WaterMinutes) };
        // If Windows can't report input, assume active rather than silently never reminding.
        var sample = new WellnessSample(DateTimeOffset.Now, NativeMethods.IdleTime() ?? TimeSpan.Zero,
            _hub.Enabled(NotificationKind.Stretch), _hub.Enabled(NotificationKind.Water), _focusRunning());
        (_state, var due) = WellnessRules.Step(_state, config, sample);
        foreach (var kind in due)
        {
            if (kind == WellnessKind.Stretch)
                _hub.Post(new("wellness:stretch", NotificationKind.Stretch, "Time to stretch", $"{settings.StretchMinutes} min at the screen. Stand up and move a little.", sample.Now));
            else
                _hub.Post(new("wellness:water", NotificationKind.Water, "Drink some water", "Stay hydrated. Take a few sips.", sample.Now));
        }
    }

    public void Dispose() => _timer.Stop();
}
