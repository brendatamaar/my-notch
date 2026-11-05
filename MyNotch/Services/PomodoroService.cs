using System.Diagnostics;
using Microsoft.UI.Dispatching;
using MyNotch.Models;
namespace MyNotch.Services;
public enum PomodoroPhase { Idle, Focus, Break, LongBreak }
/// <summary>UI-clock shell around the pure focus timer. Uses measured elapsed time, not assumed one-second ticks.</summary>
public sealed class PomodoroService
{
    public static readonly TimeSpan DefaultFocusLength=TimeSpan.FromMinutes(25);
    public static readonly TimeSpan DefaultBreakLength=TimeSpan.FromMinutes(5);
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;
    private long _lastTick;
    public FocusConfig Config {get;private set;}
    /// <summary>New lengths apply immediately when idle; a running/paused phase keeps its remaining time and later phases use the new lengths.</summary>
    public void Configure(FocusConfig config)
    {
        if(config==Config) return;
        Config=config;
        if(State.Run==FocusRun.Idle) State=State with {Remaining=config.Duration(State.Mode)};
        Changed?.Invoke(this,EventArgs.Empty);
    }
    public FocusTimerState State {get;private set;}
    public TimeSpan FocusLength=>Config.Focus;
    public TimeSpan BreakLength=>Config.ShortBreak;
    public TimeSpan Remaining=>State.Remaining;
    public bool IsRunning=>State.Run==FocusRun.Running;
    public PomodoroPhase Phase=>State.Run==FocusRun.Idle?PomodoroPhase.Idle:State.Mode switch {FocusMode.Focus=>PomodoroPhase.Focus, FocusMode.LongBreak=>PomodoroPhase.LongBreak,_=>PomodoroPhase.Break};
    public event EventHandler? Changed;
    public event EventHandler<PomodoroPhase>? PhaseCompleted;
    public PomodoroService(DispatcherQueue? dispatcher=null,TimeSpan? focusLength=null,TimeSpan? breakLength=null)
    {
        _dispatcher=dispatcher;
        Config=new(focusLength??DefaultFocusLength,breakLength??DefaultBreakLength,TimeSpan.FromMinutes(15));
        State=FocusTimer.Initial(Config);
    }
    public void Toggle()=>Dispatch(new FocusInput.Toggle());
    public void Reset()=>Dispatch(new FocusInput.Reset());
    public void Skip()=>Dispatch(new FocusInput.Skip());
    public void Select(FocusMode mode)=>Dispatch(new FocusInput.Select(mode));
    public void StartFocus(){Select(FocusMode.Focus);if(!IsRunning)Toggle();}
    public void StartBreak(){Select(FocusMode.ShortBreak);if(!IsRunning)Toggle();}
    public void Stop(){State=FocusTimer.Initial(Config);_timer?.Stop();Changed?.Invoke(this,EventArgs.Empty);}
    public void Advance(TimeSpan elapsed)=>Dispatch(new FocusInput.Tick(elapsed));
    private void Dispatch(FocusInput input)
    {
        if(input is not FocusInput.Tick && IsRunning && _dispatcher is not null)
            Advance(Stopwatch.GetElapsedTime(_lastTick));
        var transition=FocusTimer.Step(State,input,Config);
        State=transition.State;
        _lastTick=Stopwatch.GetTimestamp();
        if(IsRunning)
        {
            if(_dispatcher is not null && _timer is null)
            {
                _timer=_dispatcher.CreateTimer();_timer.Interval=TimeSpan.FromSeconds(1);_timer.IsRepeating=true;
                _timer.Tick+=(_,_)=>Advance(Stopwatch.GetElapsedTime(_lastTick));
            }
            _timer?.Start();
        }
        else _timer?.Stop();
        foreach(var phase in transition.Completed) PhaseCompleted?.Invoke(this,phase==FocusMode.Focus?PomodoroPhase.Focus:PomodoroPhase.Break);
        Changed?.Invoke(this,EventArgs.Empty);
    }
}
