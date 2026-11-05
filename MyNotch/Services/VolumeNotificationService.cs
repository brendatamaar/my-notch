using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.UI.Dispatching;
using Windows.Media.Devices;

namespace MyNotch.Services;

[GeneratedComInterface, Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IVolumeNotification
{
    [PreserveSig] int OnNotify(nint data);
}
[GeneratedComClass]
internal sealed partial class VolumeCallback : IVolumeNotification
{
    public Action<int, bool>? Changed;
    public unsafe int OnNotify(nint data)
    {
        if (data != 0)
        {
            var level = *(float*)(data + 20);
            if (float.IsFinite(level) && level is >= 0 and <= 1)
                Changed?.Invoke((int)Math.Round(level * 100), Marshal.ReadInt32(data, 16) != 0);
        }
        return 0;
    }
}
/// <summary>Core Audio callbacks; source-generated COM is compatible with Native AOT.</summary>
public sealed unsafe partial class VolumeNotificationService : IDisposable
{
    private nint _enumerator, _endpoint;
    private void* _callbackPointer;
    private readonly VolumeCallback _callback = new();
    private readonly DispatcherQueue _dispatcher;
    private (int Level, bool Muted)? _previous;
    private bool _disposed;
    public bool IsRunning => !_disposed && _endpoint != 0;
    public event Action<int, bool>? Changed;
    public VolumeNotificationService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _callback.Changed = (level, muted) => _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            var current = (level, muted);
            if (_previous == current) return;
            _previous = current; Changed?.Invoke(level, muted);
        });
    }
    public void Start()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out _enumerator));
        _callbackPointer = ComInterfaceMarshaller<IVolumeNotification>.ConvertToUnmanaged(_callback);
        MediaDevice.DefaultAudioRenderDeviceChanged += DeviceChanged;
        Bind();
    }
    private void DeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args) => _dispatcher.TryEnqueue(() => { if (!_disposed) Bind(); });
    private void Bind()
    {
        ReleaseEndpoint();
        var getDefault = (delegate* unmanaged[Stdcall]<nint, int, int, out nint, int>)(*(nint**)_enumerator)[4];
        if (getDefault(_enumerator, 0, 1, out var device) < 0) return;
        try
        {
            var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            var activate = (delegate* unmanaged[Stdcall]<nint, in Guid, uint, nint, out nint, int>)(*(nint**)device)[3];
            if (activate(device, in iid, 23, 0, out _endpoint) < 0) { _endpoint = 0; return; }
            var table = *(nint**)_endpoint;
            var getLevel = (delegate* unmanaged[Stdcall]<nint, out float, int>)table[9];
            var getMute = (delegate* unmanaged[Stdcall]<nint, out int, int>)table[15];
            if (getLevel(_endpoint, out float scalar) >= 0 && getMute(_endpoint, out int muted) >= 0) _previous = ((int)Math.Round(scalar * 100), muted != 0);
            var register = (delegate* unmanaged[Stdcall]<nint, void*, int>)table[3];
            Marshal.ThrowExceptionForHR(register(_endpoint, _callbackPointer));
        }
        finally { Marshal.Release(device); }
    }
    private void ReleaseEndpoint()
    {
        if (_endpoint == 0) return;
        var unregister = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(nint**)_endpoint)[4];
        unregister(_endpoint, _callbackPointer); Marshal.Release(_endpoint); _endpoint = 0;
    }
    public void Dispose()
    {
        _disposed = true; MediaDevice.DefaultAudioRenderDeviceChanged -= DeviceChanged; ReleaseEndpoint();
        if (_callbackPointer != null) { ComInterfaceMarshaller<IVolumeNotification>.Free(_callbackPointer); _callbackPointer = null; }
        if (_enumerator != 0) { Marshal.Release(_enumerator); _enumerator = 0; }
    }
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
}
