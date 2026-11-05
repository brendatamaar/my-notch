using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyNotch.Models;

namespace MyNotch.ViewModels;

/// <summary>Latest agent alert + bounded history. Plain INotifyPropertyChanged (AGENTS.md). UI-thread only.</summary>
public sealed class AgentNotificationViewModel : INotifyPropertyChanged
{
    internal const int MaxHistory = 20;

    private AgentPayload? _latest;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentPayload> History { get; } = new();

    public AgentPayload? Latest
    {
        get => _latest;
        private set
        {
            if (Set(ref _latest, value))
            {
                OnPropertyChanged(nameof(HasAlert));
                OnPropertyChanged(nameof(AgentName));
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool HasAlert => Latest is not null;
    public string AgentName => Latest?.Agent ?? string.Empty;
    public string Message => Latest?.Message ?? string.Empty;
    public AgentStatus Status => Latest?.Status ?? AgentStatus.Unknown;

    public string StatusText => Latest is null
        ? string.Empty
        : $"{Latest.Agent}: {Latest.Status}";

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void Apply(AgentPayload payload)
    {
        Latest = payload;
        History.Insert(0, payload);
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
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
