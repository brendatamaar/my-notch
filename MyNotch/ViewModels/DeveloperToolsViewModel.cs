using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyNotch.Models;

namespace MyNotch.ViewModels;

/// <summary>
/// Phase 3 developer-tools state: clipboard analysis + listening ports.
/// Plain INotifyPropertyChanged, no MVVM framework (AGENTS.md). UI-thread only.
/// </summary>
public sealed class DeveloperToolsViewModel : INotifyPropertyChanged
{
    private ClipboardDetection _detection = ClipboardDetection.None;
    private string _portsStatus = "Ports not loaded yet.";
    private bool _isRefreshingPorts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ListeningPortInfo> Ports { get; } = new();

    public ClipboardDetection Detection
    {
        get => _detection;
        private set
        {
            if (Set(ref _detection, value))
            {
                OnPropertyChanged(nameof(HasClipboardAction));
                OnPropertyChanged(nameof(NoClipboardAction));
                OnPropertyChanged(nameof(ClipboardActionLabel));
                OnPropertyChanged(nameof(HasSwatch));
                OnPropertyChanged(nameof(SwatchBrush));
            }
        }
    }

    public bool HasClipboardAction => Detection.HasAction;
    public bool NoClipboardAction => !Detection.HasAction;
    public string ClipboardActionLabel => Detection.ActionLabel;
    public bool HasSwatch => Detection.ColorArgb is not null;
    public Microsoft.UI.Xaml.Media.Brush? SwatchBrush => Detection.ColorArgb is { } argb
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb))
        : null;

    // ---------------------------------------------------------------- clipboard history (memory only)

    internal const int MaxClips = 10;
    public ObservableCollection<ClipboardClip> Clips { get; } = new();
    public bool HasClips => Clips.Count > 0;

    public void AddClip(string text)
    {
        var clean = text.Trim();
        if (clean.Length == 0) return;
        for (int i = Clips.Count - 1; i >= 0; i--) if (Clips[i].Text == text) Clips.RemoveAt(i);
        string preview = clean.ReplaceLineEndings(" ");
        if (preview.Length > 90) preview = preview[..90] + "…";
        Clips.Insert(0, new ClipboardClip(text.Length > 64 * 1024 ? text[..(64 * 1024)] : text, preview, Detection.HasAction ? Detection.Kind.ToString() : "", DateTimeOffset.Now));
        while (Clips.Count > MaxClips) Clips.RemoveAt(Clips.Count - 1);
        OnPropertyChanged(nameof(HasClips));
    }

    public void ClearClips() { Clips.Clear(); OnPropertyChanged(nameof(HasClips)); }

    // ---------------------------------------------------------------- containers and repositories

    private string _containersStatus = "";
    private bool _dockerAvailable;
    public ObservableCollection<DockerContainerItem> Containers { get; } = new();
    public string ContainersStatus { get => _containersStatus; private set => Set(ref _containersStatus, value); }
    public bool DockerAvailable { get => _dockerAvailable; private set => Set(ref _dockerAvailable, value); }

    public void SetContainers(bool available, IReadOnlyList<DockerContainerItem> containers, string status)
    {
        DockerAvailable = available;
        Containers.Clear();
        foreach (var c in containers) Containers.Add(c);
        ContainersStatus = status;
    }

    public void RemoveContainer(string id)
    {
        for (int i = Containers.Count - 1; i >= 0; i--) if (Containers[i].Id == id) Containers.RemoveAt(i);
        ContainersStatus = $"{Containers.Count} running";
    }

    public ObservableCollection<RepoItem> Repos { get; } = new();
    public bool HasRepos => Repos.Count > 0;

    public void SetRepos(IReadOnlyList<RepoItem> repos)
    {
        Repos.Clear();
        foreach (var r in repos) Repos.Add(r);
        OnPropertyChanged(nameof(HasRepos));
    }

    public string PortsStatus
    {
        get => _portsStatus;
        private set => Set(ref _portsStatus, value);
    }

    public bool IsRefreshingPorts
    {
        get => _isRefreshingPorts;
        private set => Set(ref _isRefreshingPorts, value);
    }

    /// <summary>Must be called on the UI thread (callers marshal via DispatcherQueue).</summary>
    public void ApplyClipboardText(string? text) => Detection = Services.ClipboardAnalyzer.Analyze(text);

    /// <summary>Must be called on the UI thread with a detached list (e.g. from Task.Run over PortMonitorService).</summary>
    public void SetPorts(IReadOnlyList<ListeningPortInfo> ports, string? status = null)
    {
        // A dual-stack listener shows up once per IP version; one row per port + process is enough.
        var seen = new HashSet<(int Port, int Pid)>();
        Ports.Clear();
        foreach (var p in ports)
            if (seen.Add((p.Port, p.Pid))) Ports.Add(p);
        PortsStatus = status ?? (Ports.Count == 0 ? "No local project dev servers detected." : $"{Ports.Count} dev server(s).");
    }

    public void BeginRefresh() => IsRefreshingPorts = true;

    public void EndRefresh(bool succeeded, string? status = null)
    {
        IsRefreshingPorts = false;
        if (status is not null) PortsStatus = status;
    }

    public void RemovePid(int pid)
    {
        for (int i = Ports.Count - 1; i >= 0; i--)
        {
            if (Ports[i].Pid == pid) Ports.RemoveAt(i);
        }
    }

    public void SetPortsError(string message)
    {
        IsRefreshingPorts = false;
        PortsStatus = message;
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
