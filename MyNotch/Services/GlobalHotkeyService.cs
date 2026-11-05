using System.Runtime.InteropServices;
using MyNotch.Interop;

namespace MyNotch.Services;

/// <summary>
/// Global hotkey via RegisterHotKey (PLAN Phase 4): Alt+Space toggles the notch
/// from any app. WM_HOTKEY is routed through a comctl32 window subclass in its
/// own slot, chaining with the clipboard subclass from Phase 3. The callback
/// runs on the window's UI thread.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    internal const int HotkeyId = 1;
    private static readonly UIntPtr SubclassId = new(0x486F746B); // "Hotk"

    private IntPtr _hwnd = IntPtr.Zero;
    private NativeMethods.SubclassProc? _proc;
    private GCHandle _selfHandle;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised on the window's UI thread when Alt+Space is pressed.</summary>
    public event EventHandler? HotkeyPressed;

    public bool IsRunning => _started && !_disposed;

    /// <summary>Returns false (instead of throwing) when registration fails, e.g. taken by another app.</summary>
    public bool TryStart(IntPtr hwnd)
    {
        if (_disposed) return false;
        if (_started) return true;
        if (hwnd == IntPtr.Zero) return false;

        _hwnd = hwnd;
        _proc = SubclassCallback;
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var refData = (UIntPtr)(nuint)(nint)GCHandle.ToIntPtr(_selfHandle);

        if (!NativeMethods.SetWindowSubclass(_hwnd, _proc, SubclassId, refData))
        {
            CleanupHandle();
            return false;
        }

        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId, NativeMethods.MOD_ALT, NativeMethods.VK_SPACE))
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HotkeyId); } catch { }
            Stop();
            return false;
        }

        _started = true;
        return true;
    }

    public void Stop()
    {
        if (!_started && _proc is null) return;
        _started = false;
        try { NativeMethods.UnregisterHotKey(_hwnd, HotkeyId); } catch { }
        try
        {
            if (_proc is not null) NativeMethods.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        }
        catch { }
        _proc = null;
        _hwnd = IntPtr.Zero;
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void CleanupHandle()
    {
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _proc = null;
        _hwnd = IntPtr.Zero;
    }

    private static IntPtr SubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_HOTKEY && wParam == (IntPtr)HotkeyId)
        {
            try
            {
                var ptr = (IntPtr)(nint)(nuint)(ulong)dwRefData;
                if (ptr != IntPtr.Zero && GCHandle.FromIntPtr(ptr).Target is GlobalHotkeyService self)
                    self.HotkeyPressed?.Invoke(self, EventArgs.Empty);
            }
            catch { }
        }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
