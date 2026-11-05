namespace MyNotch.Models;

public enum WellnessKind { Stretch, Water }

/// <param name="Stretch">Continuous active time before a stretch reminder.</param>
/// <param name="Water">Active time before a drink-water reminder.</param>
/// <param name="AwayAfter">No keyboard/mouse input for this long means the user stepped away.</param>
public sealed record WellnessConfig(TimeSpan Stretch, TimeSpan Water, TimeSpan AwayAfter)
{
    public static WellnessConfig Default => new(TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(5));
    /// <summary>Longest gap one tick may count; a sleep/resume or clock jump must not fire reminders on wake.</summary>
    public static readonly TimeSpan MaxTick = TimeSpan.FromMinutes(2);
}

/// <summary>Active time counted towards each reminder, and the instant of the previous sample.</summary>
public sealed record WellnessState(TimeSpan SinceStretch, TimeSpan SinceWater, DateTimeOffset? LastTick)
{
    public static WellnessState Initial => new(TimeSpan.Zero, TimeSpan.Zero, null);
}

/// <summary>One sample of the outside world, taken by the shell every tick.</summary>
/// <param name="IdleFor">Time since the last keyboard or mouse input.</param>
/// <param name="FocusRunning">A Pomodoro session is running; its own breaks handle stretching.</param>
public sealed record WellnessSample(DateTimeOffset Now, TimeSpan IdleFor, bool StretchEnabled, bool WaterEnabled, bool FocusRunning);

/// <summary>
/// Stretch: after <see cref="WellnessConfig.Stretch"/> of continuous activity; stepping away resets it (that was the break),
/// and a running Pomodoro holds it at zero. Water: after <see cref="WellnessConfig.Water"/> of active time; stepping away
/// only pauses it. A disabled reminder stays at zero so turning it on starts a fresh interval.
/// </summary>
public static class WellnessRules
{
    public static (WellnessState State, IReadOnlyList<WellnessKind> Due) Step(WellnessState state, WellnessConfig config, WellnessSample sample)
    {
        var elapsed = state.LastTick is { } last ? sample.Now - last : TimeSpan.Zero;
        if (elapsed < TimeSpan.Zero || elapsed > WellnessConfig.MaxTick) elapsed = TimeSpan.Zero;
        bool away = sample.IdleFor >= config.AwayAfter;

        var stretch = !sample.StretchEnabled || away || sample.FocusRunning ? TimeSpan.Zero : state.SinceStretch + elapsed;
        var water = !sample.WaterEnabled ? TimeSpan.Zero : away ? state.SinceWater : state.SinceWater + elapsed;

        var due = new List<WellnessKind>(2);
        if (sample.StretchEnabled && stretch >= config.Stretch) { due.Add(WellnessKind.Stretch); stretch = TimeSpan.Zero; }
        if (sample.WaterEnabled && water >= config.Water) { due.Add(WellnessKind.Water); water = TimeSpan.Zero; }
        return (new WellnessState(stretch, water, sample.Now), due);
    }
}
