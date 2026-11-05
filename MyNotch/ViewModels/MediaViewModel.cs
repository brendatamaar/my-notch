using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using MyNotch.Models;

namespace MyNotch.ViewModels;

/// <summary>Current media track display state. Plain INotifyPropertyChanged (AGENTS.md). UI-thread only.</summary>
public sealed class MediaViewModel : INotifyPropertyChanged
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private MediaTrackInfo _current = MediaTrackInfo.NoMedia;
    private ImageSource? _art;
    private string _currentLine = string.Empty;
    private string _nextLine = string.Empty;
    private double _progress;
    private string _previousLine = string.Empty;
    private string _plainLyrics = string.Empty;
    private string _lyricsStatus = "Lyrics will appear here";
    private string _elapsedTime = "0:00";
    private string _remainingTime = "--:--";
    private LyricsAvailability _lyricsAvailability = LyricsAvailability.Missing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaTrackInfo Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
            {
                OnPropertyChanged(nameof(HasMedia));
                OnPropertyChanged(nameof(NoMedia));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(PlayPauseLabel));
                OnPropertyChanged(nameof(PlayPauseGlyph));
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Artist));
                OnPropertyChanged(nameof(SourceLabel));
            }
        }
    }

    public bool HasMedia => Current.HasSession;
    public bool NoMedia => !Current.HasSession;
    public string DisplayText => Current.DisplayText;
    public string PlayPauseLabel => Current.PlayPauseLabel;
    public string PlayPauseGlyph => Current.IsPlaying ? PauseGlyph : PlayGlyph;
    public bool IsPlaying => Current.IsPlaying;
    public bool IsPaused => !IsPlaying;
    public string Title => string.IsNullOrWhiteSpace(Current.Title) ? "Unknown title" : Current.Title;
    public string Artist => Current.Artist;
    public string SourceLabel => MediaMetadata.ProviderName(Current.SourceApp);

    public ImageSource? Art
    {
        get => _art;
        private set
        {
            if (Set(ref _art, value))
            {
                OnPropertyChanged(nameof(HasArt));
                OnPropertyChanged(nameof(NoArt));
            }
        }
    }

    public bool HasArt => _art is not null;
    public bool NoArt => _art is null;

    public string CurrentLine
    {
        get => _currentLine;
        private set { if (Set(ref _currentLine, value)) OnPropertyChanged(nameof(HasLyrics)); }
    }

    public string NextLine
    {
        get => _nextLine;
        private set => Set(ref _nextLine, value);
    }

    public bool HasLyrics => !string.IsNullOrWhiteSpace(_currentLine);
    public string PreviousLine => _previousLine;
    public string PlainLyrics => _plainLyrics;
    public string LyricsStatus => _lyricsStatus;
    public string ElapsedTime => _elapsedTime;
    public string RemainingTime => _remainingTime;
    public bool HasSyncedLyrics => _lyricsAvailability == LyricsAvailability.Synced;
    public bool HasPlainLyrics => _lyricsAvailability == LyricsAvailability.Plain;
    public bool ShowLyricsPlaceholder => !HasSyncedLyrics && !HasPlainLyrics;
    public bool ShowLyricsCaption => HasSyncedLyrics || HasPlainLyrics;

    public void SetLyricsAvailability(LyricsAvailability availability, string status, string plain = "")
    {
        _lyricsAvailability = availability;
        _lyricsStatus = status;
        _plainLyrics = plain;
        foreach (var name in new[] { nameof(LyricsStatus), nameof(PlainLyrics), nameof(HasSyncedLyrics),
            nameof(HasPlainLyrics), nameof(ShowLyricsPlaceholder), nameof(ShowLyricsCaption) }) OnPropertyChanged(name);
    }

    public void SetPreviousLine(string value)
    {
        if (_previousLine == value) return;
        _previousLine = value;
        OnPropertyChanged(nameof(PreviousLine));
    }

    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        Set(ref _elapsedTime, FormatTime(position), nameof(ElapsedTime));
        Set(ref _remainingTime, duration > TimeSpan.Zero ? "\u2212" + FormatTime(duration - position) : "--:--", nameof(RemainingTime));
    }

    private static string FormatTime(TimeSpan time) =>
        $"{(int)Math.Max(0, time.TotalMinutes)}:{Math.Max(0, time.Seconds):00}";

    /// <summary>0..1 through the song; 0 when the player gives no duration.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void Apply(MediaTrackInfo track) => Current = track;

    public void SetArt(ImageSource? art) => Art = art;

    public void SetLyric(string current, string next)
    {
        CurrentLine = current;
        NextLine = next;
    }

    public void SetProgress(double value) => Progress = Math.Clamp(value, 0, 1);

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

public enum LyricsAvailability { Loading, Synced, Plain, Instrumental, Missing, Failed }
