using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyNotch.Models;

namespace MyNotch.ViewModels;

/// <summary>Palette query + match list state. Plain INotifyPropertyChanged (AGENTS.md). UI-thread only.</summary>
public sealed class CommandPaletteViewModel : INotifyPropertyChanged
{
    private string _query = string.Empty;
    private int _selectedIndex = -1;
    private string _status = "Saved actions";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SavedCommand> Matches { get; } = new();

    public string Query
    {
        get => _query;
        set => Set(ref _query, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        private set => Set(ref _selectedIndex, value);
    }

    public string StatusText
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public SavedCommand? SelectedCommand =>
        SelectedIndex >= 0 && SelectedIndex < Matches.Count ? Matches[SelectedIndex] : null;

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void SetMatches(IReadOnlyList<SavedCommand> matches, string query)
    {
        Matches.Clear();
        foreach (var m in matches) Matches.Add(m);
        SelectedIndex = Matches.Count > 0 ? 0 : -1;
        StatusText = Matches.Count == 0
            ? (string.IsNullOrWhiteSpace(query) ? "No saved commands." : $"No match for \"{query.Trim()}\".")
            : $"{Matches.Count} saved action(s).";
    }

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void MoveSelection(int delta)
    {
        if (Matches.Count == 0) return;
        var next = SelectedIndex + delta;
        if (next < 0) next = Matches.Count - 1;
        if (next >= Matches.Count) next = 0;
        SelectedIndex = next;
    }

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void SetStatus(string status) => StatusText = status;

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
