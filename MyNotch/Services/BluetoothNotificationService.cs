using Windows.Devices.Enumeration;

namespace MyNotch.Services;

public enum BluetoothChange { Connected, Disconnected, Added }

/// <summary>
/// AEP events for paired Bluetooth devices; initial enumeration stays quiet. The watcher only sees paired
/// endpoints, so a new endpoint after enumeration means a device was just paired ("added").
/// </summary>
public sealed class BluetoothNotificationService : IDisposable
{
    private readonly Dictionary<string, (string Name, bool Connected)> _devices = new();
    private readonly Dictionary<string, DateTimeOffset> _addedAt = new();
    private static readonly TimeSpan AddedQuietPeriod = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private DeviceWatcher? _watcher;
    private bool _primed, _disposed;
    public event Action<string, BluetoothChange>? Changed;
    public bool IsRunning => !_disposed && _watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted;
    public void Start()
    {
        // Use a concrete array at the WinRT boundary. C# collection-expression helper types
        // cannot be projected as IIterable<string> by the Windows SDK's AOT marshaller.
        _watcher = DeviceInformation.CreateWatcher("(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True", new[] { "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint);
        _watcher.Added += Added; _watcher.Updated += Updated; _watcher.Removed += Removed; _watcher.EnumerationCompleted += Completed;
        _watcher.Start();
    }
    private void Added(DeviceWatcher sender, DeviceInformation device)
    {
        bool connected = device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var value) && value is true;
        bool notify;
        lock (_gate)
        {
            if (_disposed) return;
            notify = _primed && !_devices.ContainsKey(device.Id); _devices[device.Id] = (device.Name, connected);
            if (notify) _addedAt[device.Id] = DateTimeOffset.UtcNow;
        }
        if (notify) Changed?.Invoke(device.Name, BluetoothChange.Added);
    }
    private void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        (string Name, bool Connected) old;
        bool connected;
        lock (_gate)
        {
            if (_disposed || !_devices.TryGetValue(update.Id, out old) || !update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var value) || value is not bool flag) return;
            connected = flag; _devices[update.Id] = (old.Name, connected);
            if (!_primed || connected == old.Connected) return;
            // A freshly paired device usually connects right away; keep "added" on screen instead of replacing it.
            if (connected && _addedAt.Remove(update.Id, out var added) && DateTimeOffset.UtcNow - added < AddedQuietPeriod) return;
        }
        Changed?.Invoke(old.Name, connected ? BluetoothChange.Connected : BluetoothChange.Disconnected);
    }
    private void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        (string Name, bool Connected) old;
        lock (_gate) { _addedAt.Remove(update.Id); if (_disposed || !_devices.Remove(update.Id, out old) || !_primed || !old.Connected) return; }
        Changed?.Invoke(old.Name, BluetoothChange.Disconnected);
    }
    private void Completed(DeviceWatcher sender, object args) { lock (_gate) _primed = true; }
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        if (_watcher is null) return;
        _watcher.Added -= Added; _watcher.Updated -= Updated; _watcher.Removed -= Removed; _watcher.EnumerationCompleted -= Completed;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
    }
}
