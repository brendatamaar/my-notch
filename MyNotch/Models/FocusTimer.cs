namespace MyNotch.Models;
public enum FocusMode { Focus, ShortBreak, LongBreak }
public enum FocusRun { Idle, Running, Paused }
public sealed record FocusConfig(TimeSpan Focus, TimeSpan ShortBreak, TimeSpan LongBreak, int Rounds = 4)
{
    public static FocusConfig Default { get; } = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    public TimeSpan Duration(FocusMode mode) => mode switch {FocusMode.Focus=>Focus, FocusMode.ShortBreak=>ShortBreak, FocusMode.LongBreak=>LongBreak, _=>throw new ArgumentOutOfRangeException(nameof(mode))};
}
public sealed record FocusTimerState(FocusMode Mode, FocusRun Run, TimeSpan Remaining, int Round = 1, int Completed = 0, int CompletedMask = 0);
public abstract record FocusInput
{
    public sealed record Toggle : FocusInput;
    public sealed record Reset : FocusInput;
    public sealed record Skip : FocusInput;
    public sealed record Select(FocusMode Mode) : FocusInput;
    public sealed record Tick(TimeSpan Elapsed) : FocusInput;
}
public static class FocusTimer
{
    public static FocusTimerState Initial(FocusConfig c)
    {
        if(c.Focus<=TimeSpan.Zero || c.ShortBreak<=TimeSpan.Zero || c.LongBreak<=TimeSpan.Zero || c.Rounds is <1 or >12) throw new ArgumentOutOfRangeException(nameof(c));
        return new(FocusMode.Focus, FocusRun.Idle, c.Focus);
    }
    private static FocusTimerState Next(FocusTimerState s, FocusConfig c, bool earned) => s.Mode == FocusMode.Focus
        ? s with {Mode=s.Round==c.Rounds?FocusMode.LongBreak:FocusMode.ShortBreak, Remaining=s.Round==c.Rounds?c.LongBreak:c.ShortBreak,
            Completed=s.Completed+(earned?1:0), CompletedMask=earned?s.CompletedMask | (1 << (s.Round-1)):s.CompletedMask}
        : s with {Mode=FocusMode.Focus, Remaining=c.Focus, Round=s.Mode==FocusMode.LongBreak?1:Math.Min(c.Rounds,s.Round+1),
            CompletedMask=s.Mode==FocusMode.LongBreak?0:s.CompletedMask};
    public static double Segment(FocusTimerState state, FocusConfig config, int round)
    {
        if(round<1 || round>config.Rounds) return 0;
        if((state.CompletedMask & (1 << (round-1)))!=0) return 1;
        return state.Mode==FocusMode.Focus && state.Run!=FocusRun.Idle && state.Round==round
            ? Math.Clamp(1-state.Remaining/config.Focus,0,1) : 0;
    }
    public static (FocusTimerState State, IReadOnlyList<FocusMode> Completed) Step(FocusTimerState s, FocusInput input, FocusConfig c)
    {
        switch(input)
        {
            case FocusInput.Toggle: return (s with {Run=s.Run==FocusRun.Running?FocusRun.Paused:FocusRun.Running}, []);
            case FocusInput.Reset: return (s with {Run=FocusRun.Idle, Remaining=c.Duration(s.Mode)}, []);
            case FocusInput.Select select when s.Run==FocusRun.Idle: return(s with {Mode=select.Mode,Remaining=c.Duration(select.Mode)}, []);
            case FocusInput.Select: return(s, []);
            case FocusInput.Skip when s.Run!=FocusRun.Idle: return(Next(s,c,false), []);
            case FocusInput.Skip: return(s, []);
            case FocusInput.Tick tick when s.Run==FocusRun.Running && tick.Elapsed>TimeSpan.Zero:
                if(tick.Elapsed<s.Remaining) return(s with {Remaining=s.Remaining-tick.Elapsed}, []);
                var finished = new List<FocusMode>{s.Mode};
                var rest=tick.Elapsed-s.Remaining;
                var next=Next(s,c,true);
                if(s.Mode!=FocusMode.Focus) return(next with {Run=FocusRun.Idle}, finished);
                if(rest>=next.Remaining) {finished.Add(next.Mode); return(Next(next,c,false) with {Run=FocusRun.Idle},finished);}
                return(next with {Remaining=next.Remaining-rest},finished);
            case FocusInput.Tick: return(s, []);
            default: throw new ArgumentOutOfRangeException(nameof(input));
        }
    }
}
