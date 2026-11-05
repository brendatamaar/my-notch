using System.Runtime.InteropServices;
using MyNotch.Interop;

namespace MyNotch.Services;

public sealed partial class BrightnessNotificationService : IDisposable
{
    private readonly NativeMethods.SubclassProc _callback;
    private nint _hwnd, _subscription;
    private int? _previous;
    private static readonly UIntPtr Id = new(0x4E425249);
    public bool IsRunning => _subscription != 0;
    public event Action<int>? Changed;
    public event Action? ClockChanged;
    public BrightnessNotificationService() => _callback = WindowProc;
    public void Start(nint hwnd)
    {
        _hwnd = hwnd;
        if (!NativeMethods.SetWindowSubclass(hwnd, _callback, Id, UIntPtr.Zero)) throw new InvalidOperationException("Brightness window hook unavailable.");
        var setting = new Guid("8ffee2c6-2d01-46be-adb9-398addc5b4ff");
        _subscription = RegisterPowerSettingNotification(hwnd, in setting, 0);
    }
    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, UIntPtr id, UIntPtr data)
    {
        if (message == 0x001E || message == 0x0218 && ((nuint)wParam == 7 || (nuint)wParam == 18)) ClockChanged?.Invoke();
        if (message == 0x0218 && (nuint)wParam == 0x8013 && lParam != 0 && Marshal.PtrToStructure<Guid>(lParam) == new Guid("8ffee2c6-2d01-46be-adb9-398addc5b4ff") && Marshal.ReadInt32(lParam, 16) == 4)
        {
            int value = Marshal.ReadInt32(lParam, 20);
            if (value is >= 0 and <= 100)
            {
                bool changed = _previous is { } before && before != value;
                _previous = value;
                if (changed) Changed?.Invoke(value);
            }
        }
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_subscription != 0) { UnregisterPowerSettingNotification(_subscription); _subscription = 0; }
        if (_hwnd != 0) { NativeMethods.RemoveWindowSubclass(_hwnd, _callback, Id); _hwnd = 0; }
    }
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnregisterPowerSettingNotification(nint handle);
}
