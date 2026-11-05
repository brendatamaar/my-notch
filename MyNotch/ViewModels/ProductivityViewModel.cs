using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyNotch.Services;
using MyNotch.Models;

namespace MyNotch.ViewModels;

/// <summary>Pomodoro countdown + next-prayer display. Plain INotifyPropertyChanged (AGENTS.md). UI-thread only.</summary>
public sealed class ProductivityViewModel : INotifyPropertyChanged
{
    private string _pomodoroText = "Focus 25:00";
    private string _timeText = "25:00";
    private string _phaseLabel = "Ready";
    private string _phase = nameof(PomodoroPhase.Idle);
    private string _prayerText = "Prayer times loading…";
    private string _prayerName = "Next prayer";
    private string _prayerClock = "--:--";
    private bool _isRunning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PomodoroText
    {
        get => _pomodoroText;
        private set => Set(ref _pomodoroText, value);
    }

    /// <summary>"25:00" — the big readout on the Focus page.</summary>
    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value);
    }

    /// <summary>"Ready", "Focus" or "Break".</summary>
    public string PhaseLabel
    {
        get => _phaseLabel;
        private set => Set(ref _phaseLabel, value);
    }

    public string Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => _focus.Run == FocusRun.Idle;
    public bool CanSkip => _focus.Run != FocusRun.Idle;

    public string PrayerText
    {
        get => _prayerText;
        private set => Set(ref _prayerText, value);
    }

    /// <summary>Prayer name, or a status such as "Prayer times offline." when nothing is scheduled.</summary>
    public string PrayerName
    {
        get => _prayerName;
        private set => Set(ref _prayerName, value);
    }

    public string PrayerClock
    {
        get => _prayerClock;
        private set => Set(ref _prayerClock, value);
    }

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    private FocusTimerState _focus=FocusTimer.Initial(FocusConfig.Default);
    private FocusConfig _config=FocusConfig.Default;
    private string _untilText="Ready to focus";
    public string ToggleLabel=>_focus.Run switch {FocusRun.Running=>"Pause",FocusRun.Paused=>"Resume",_=>"Start"};
    public string UntilText=>_untilText;
    public double Progress=>Math.Clamp(1-_focus.Remaining/_config.Duration(_focus.Mode),0,1);
    public string RoundLabel=>$"{(_focus.Mode==FocusMode.Focus?"Focus":_focus.Mode==FocusMode.LongBreak?"Long break":"Short break")} · {_focus.Round} of {_config.Rounds}";
    public string CycleSummary
    {
        get
        {
            var remaining=_focus.Remaining;
            if(_focus.Mode==FocusMode.Focus) remaining+=(_config.Rounds-_focus.Round)*(_config.Focus+_config.ShortBreak)+_config.LongBreak;
            else if(_focus.Mode==FocusMode.ShortBreak) remaining+=(_config.Rounds-_focus.Round)*_config.Focus+Math.Max(0,_config.Rounds-_focus.Round-1)*_config.ShortBreak+_config.LongBreak;
            var minutes=(int)Math.Ceiling(remaining.TotalMinutes);
            return $"{minutes/60} h {minutes%60:00} min left · {_focus.Completed} completed";
        }
    }
    private double Segment(int round)=>FocusTimer.Segment(_focus,_config,round);
    public double Round1=>Segment(1);
    public double Round2=>Segment(2);
    public double Round3=>Segment(3);
    public double Round4=>Segment(4);
    public double LongProgress=>_focus.Mode==FocusMode.LongBreak?Progress:0;
    public string Fajr {get;private set;}="--:--";
    public string Dhuhr {get;private set;}="--:--";
    public string Asr {get;private set;}="--:--";
    public string Maghrib {get;private set;}="--:--";
    public string Isha {get;private set;}="--:--";

    public void ApplyPomodoro(PomodoroService timer)
    {
        _focus=timer.State;_config=timer.Config;
        Phase=timer.Phase.ToString();IsRunning=timer.IsRunning;
        PhaseLabel=_focus.Run==FocusRun.Paused?"Paused":_focus.Mode==FocusMode.Focus?"Focus":_focus.Mode==FocusMode.LongBreak?"Long break":"Short break";
        TimeText=GlanceResolver.FormatClock(_focus.Remaining);
        PomodoroText=$"{PhaseLabel} {TimeText}";
        _untilText=_focus.Run==FocusRun.Running?$"until {DateTime.Now+_focus.Remaining:HH:mm}":_focus.Run==FocusRun.Paused?"Paused":"Ready when you are";
        foreach(var name in new[]{nameof(IsIdle),nameof(CanSkip),nameof(ToggleLabel),nameof(UntilText),nameof(Progress),nameof(RoundLabel),nameof(CycleSummary),nameof(Round1),nameof(Round2),nameof(Round3),nameof(Round4),nameof(LongProgress)})OnPropertyChanged(name);
    }
    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void ApplyPrayer(string? name, DateTime? at, string status, PrayerTimes? day = null)
    {
        Fajr=day?.Fajr.ToString("HH:mm")??"--:--"; Dhuhr=day?.Dhuhr.ToString("HH:mm")??"--:--";
        Asr=day?.Asr.ToString("HH:mm")??"--:--";Maghrib=day?.Maghrib.ToString("HH:mm")??"--:--";Isha=day?.Isha.ToString("HH:mm")??"--:--";
        foreach(var field in new[]{nameof(Fajr),nameof(Dhuhr),nameof(Asr),nameof(Maghrib),nameof(Isha)})OnPropertyChanged(field);
        PrayerText = name is not null && at is not null
            ? $"{name} at {at:HH:mm}"
            : status;
        PrayerName = name is not null && at is not null ? name : status;
        PrayerClock = name is not null && at is not null ? $"{at:HH:mm}" : "--:--";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
