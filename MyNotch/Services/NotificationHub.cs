using Microsoft.UI.Dispatching;
using MyNotch.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyNotch.Services;

public sealed record NotificationPreferences(NotificationKind[] Disabled);
[JsonSerializable(typeof(NotificationPreferences))]
internal partial class NotificationJsonContext : JsonSerializerContext;

/// <summary>UI-thread inbox; private message contents are kept only in bounded memory.</summary>
public sealed class NotificationHub : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", "notification-settings.json");
    private HashSet<NotificationKind> _disabled = new();
    private NotificationQueue _state = NotificationQueue.Empty;
    private readonly List<NotchNotification> _history = new();
    private bool _disposed;
    public NotchNotification? Current => _state.Current;
    public DateTimeOffset Until => _state.Until;
    public IReadOnlyList<NotchNotification> History => _history;
    public event EventHandler? Changed;
    public NotificationHub(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length < 16_384)
                _disabled = (JsonSerializer.Deserialize(File.ReadAllText(_path), NotificationJsonContext.Default.NotificationPreferences)?.Disabled ?? []).Where(Enum.IsDefined).ToHashSet();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        _timer = dispatcher.CreateTimer(); _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Advance(false);
    }
    public bool Enabled(NotificationKind kind) => !_disabled.Contains(kind);
    public bool SetEnabled(NotificationKind kind, bool value)
    {
        var next = new HashSet<NotificationKind>(_disabled);
        if (value) next.Remove(kind); else next.Add(kind);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new NotificationPreferences(next.ToArray()), NotificationJsonContext.Default.NotificationPreferences));
            File.Move(_path + ".tmp", _path, true); _disabled = next;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        if (!value)
        {
            _state = _state with { Pending = _state.Pending.Where(p => p.Kind != kind).ToArray() };
            if (_state.Current?.Kind == kind) Advance(true);
        }
        Changed?.Invoke(this, EventArgs.Empty); return true;
    }
    public void Post(NotchNotification notice) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed || !Enabled(notice.Kind)) return;
        // Changes from the same HUD belong to one history item.
        _history.RemoveAll(n => n.Key == notice.Key);
        _history.Insert(0, notice);
        if (_history.Count > 40) _history.RemoveRange(40, _history.Count - 40);
        _state = NotificationRules.Step(_state, new NotificationInput.Receive(notice), DateTimeOffset.Now);
        Arm(); Changed?.Invoke(this, EventArgs.Empty);
    });
    public void Dismiss() => Advance(true);
    private void Advance(bool dismiss)
    {
        _state = NotificationRules.Step(_state, new NotificationInput.Advance(dismiss), DateTimeOffset.Now);
        Arm(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Arm()
    {
        _timer.Stop();
        if (_state.Current is null || _disposed) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, (_state.Until - DateTimeOffset.Now).TotalMilliseconds)); _timer.Start();
    }
    public void Dispose() { _disposed = true; _timer.Stop(); }
}
