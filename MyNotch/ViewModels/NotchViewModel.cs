using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyNotch.ViewModels;

public enum NotchState { Collapsed, Expanded }

public enum NotchTab { Music, Calendar, Focus, Prayer, Agents, System, Dev, Run, Settings }

/// <summary>Localized state only — simple INotifyPropertyChanged, no MVVM framework (AGENTS.md).</summary>
public sealed class NotchViewModel : INotifyPropertyChanged
{
    private NotchState _state = NotchState.Collapsed;
    private NotchTab _selectedTab = NotchTab.Music;
    private string _statusText = "MyNotch";

    public event PropertyChangedEventHandler? PropertyChanged;

    public NotchState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) OnPropertyChanged(nameof(IsExpanded)); }
    }

    public bool IsExpanded => State == NotchState.Expanded;

    public NotchTab SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    /// <summary>Footer line of the open notch: webhook address, hotkey conflicts, last command.</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public void Toggle() => State = IsExpanded ? NotchState.Collapsed : NotchState.Expanded;

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
