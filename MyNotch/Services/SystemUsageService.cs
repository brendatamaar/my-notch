using Microsoft.UI.Dispatching;
using MyNotch.Models;

namespace MyNotch.Services;

public sealed class SystemUsageService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly SystemUsageReader _reader = new();
    private bool _active, _disposed, _reset;
    private int _generation, _sampling;
    public event EventHandler<SystemUsageSnapshot>? Changed;
    public event EventHandler? SamplingFailed;
    public SystemUsageService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher; _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1); _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Sample();
    }
    public void Start() { if (_active || _disposed) return; _active = true; _reset = true; _generation++; Sample(); _timer.Start(); }
    public void Stop() { _active = false; _generation++; _timer.Stop(); }
    public void RefreshStorage() { _reset = true; Sample(); }
    private async void Sample()
    {
        if (!_active || _disposed || Interlocked.Exchange(ref _sampling, 1) != 0) return;
        int generation = _generation; bool reset = _reset; _reset = false;
        SystemUsageSnapshot? snapshot = null;
        try { snapshot = await Task.Run(() => _reader.Capture(reset)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _dispatcher.TryEnqueue(() => { if (!_disposed && _active && generation == _generation) SamplingFailed?.Invoke(this, EventArgs.Empty); });
        }
        finally { Interlocked.Exchange(ref _sampling, 0); }
        if (snapshot is null) return;
        _dispatcher.TryEnqueue(() => { if (!_disposed && _active && generation == _generation) Changed?.Invoke(this, snapshot); });
    }
    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; _reader.Dispose(); }
}
