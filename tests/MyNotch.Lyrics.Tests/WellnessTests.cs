using MyNotch.Models;

internal static class WellnessTests
{
    public static void Run()
    {
        var config = new WellnessConfig(TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(5));
        var start = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
        var tick = TimeSpan.FromSeconds(30);
        WellnessSample Active(DateTimeOffset at, bool focus = false) => new(at, TimeSpan.FromSeconds(3), true, true, focus);

        // Continuous activity: water at 45 min, stretch at 50 min, each exactly once per interval.
        var state = WellnessState.Initial; var fired = new List<(WellnessKind Kind, TimeSpan At)>();
        for (int i = 0; i <= 200; i++)
        {
            var at = start + tick * i;
            (state, var due) = WellnessRules.Step(state, config, Active(at));
            fired.AddRange(due.Select(k => (k, at - start)));
        }
        Check(fired.Where(f => f.Kind == WellnessKind.Water).Select(f => f.At).SequenceEqual(new[] { 45, 90 }.Select(m => TimeSpan.FromMinutes(m))), "water every 45 active minutes");
        Check(fired.Where(f => f.Kind == WellnessKind.Stretch).Select(f => f.At).SequenceEqual(new[] { 50, 100 }.Select(m => TimeSpan.FromMinutes(m))), "stretch every 50 active minutes");

        // Stepping away resets stretch (that was the break) but only pauses water.
        state = new WellnessState(TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(40), start);
        (state, _) = WellnessRules.Step(state, config, new(start + tick, TimeSpan.FromMinutes(6), true, true, false));
        Check(state.SinceStretch == TimeSpan.Zero && state.SinceWater == TimeSpan.FromMinutes(40), "away resets stretch, pauses water");

        // A running Pomodoro holds stretch at zero; water still counts.
        state = new WellnessState(TimeSpan.FromMinutes(49), TimeSpan.FromMinutes(10), start);
        (state, var focusDue) = WellnessRules.Step(state, config, Active(start + tick, focus: true));
        Check(focusDue.Count == 0 && state.SinceStretch == TimeSpan.Zero && state.SinceWater == TimeSpan.FromMinutes(10.5), "focus suppresses stretch only");

        // Sleep/resume or a clock jump can't fire reminders on wake.
        state = new WellnessState(TimeSpan.FromMinutes(49), TimeSpan.FromMinutes(44), start);
        (state, var wake) = WellnessRules.Step(state, config, Active(start.AddHours(8)));
        Check(wake.Count == 0 && state.SinceStretch == TimeSpan.FromMinutes(49), "long gap counts as nothing");
        (_, var backwards) = WellnessRules.Step(state, config, Active(start.AddHours(-1)));
        Check(backwards.Count == 0, "clock moving backwards counts as nothing");

        // Disabled reminders never fire and restart from zero when turned on.
        state = new WellnessState(TimeSpan.FromMinutes(49), TimeSpan.FromMinutes(44), start);
        (state, var off) = WellnessRules.Step(state, config, new(start + tick, TimeSpan.Zero, false, false, false));
        Check(off.Count == 0 && state.SinceStretch == TimeSpan.Zero && state.SinceWater == TimeSpan.Zero, "disabled reminders reset");

        // Exhaustive short sequences: counters bounded, never negative, and a reminder always resets its counter.
        var gaps = new[] { TimeSpan.Zero, tick, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(-1) };
        var idles = new[] { TimeSpan.Zero, TimeSpan.FromMinutes(10) };
        int paths = 0;
        void Explore(WellnessState s, DateTimeOffset now, int depth)
        {
            Check(s.SinceStretch >= TimeSpan.Zero && s.SinceStretch < config.Stretch && s.SinceWater >= TimeSpan.Zero && s.SinceWater < config.Water, "counters stay within one interval");
            if (depth == 0) { paths++; return; }
            foreach (var gap in gaps) foreach (var idle in idles) foreach (int flags in Enumerable.Range(0, 8))
            {
                var at = now + gap;
                var (next, due) = WellnessRules.Step(s, config, new(at, idle, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0));
                Check(due.Distinct().Count() == due.Count, "each reminder at most once per sample");
                Check(next.SinceStretch <= s.SinceStretch + WellnessConfig.MaxTick && next.SinceWater <= s.SinceWater + WellnessConfig.MaxTick, "one sample adds at most one tick");
                Explore(next, at, depth - 1);
            }
        }
        Explore(new WellnessState(TimeSpan.FromMinutes(48), TimeSpan.FromMinutes(44), start), start, 3);
        Console.WriteLine($"PASS: stretch/water intervals, away/focus/sleep/disabled handling and {paths} sample sequences");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Wellness invariant: " + message); }
}
