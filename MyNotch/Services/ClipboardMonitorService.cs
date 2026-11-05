using System.Runtime.InteropServices;
using MyNotch.Interop;

namespace MyNotch.Services;

/// <summary>
/// Event-driven clipboard watcher (PLAN Phase 3, AGENTS.md: no polling loops).
/// Subscribes one HWND via AddClipboardFormatListener and receives WM_CLIPBOARDUPDATE
/// through a comctl32 window subclass. The callback fires on the window's UI thread;
/// subscribers fetch Clipboard.GetContent() asynchronously and marshal via DispatcherQueue.
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    private static readonly UIntPtr SubclassId = new(0x4E6F7463); // "Notc"

    private IntPtr _hwnd = IntPtr.Zero;
    private NativeMethods.SubclassProc? _proc;
    private GCHandle _selfHandle;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised on the window's UI thread when clipboard content changes. No payload: fetch on demand.</summary>
    public event EventHandler? ClipboardChanged;

    public bool IsRunning => _started && !_disposed;

    public void Start(IntPtr hwnd)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClipboardMonitorService));
        if (_started) return;
        if (hwnd == IntPtr.Zero) throw new ArgumentException("HWND must be non-zero.", nameof(hwnd));

        _hwnd = hwnd;
        _proc = SubclassCallback;
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var refData = (UIntPtr)(nuint)(nint)GCHandle.ToIntPtr(_selfHandle);

        if (!NativeMethods.SetWindowSubclass(_hwnd, _proc, SubclassId, refData))
        {
            _selfHandle.Free();
            _proc = null;
            throw new InvalidOperationException("SetWindowSubclass failed.");
        }

        if (!NativeMethods.AddClipboardFormatListener(_hwnd))
        {
            try { NativeMethods.RemoveWindowSubclass(_hwnd, _proc, SubclassId); } catch { }
            _selfHandle.Free();
            _proc = null;
            throw new InvalidOperationException("AddClipboardFormatListener failed.");
        }

        _started = true;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { NativeMethods.RemoveClipboardFormatListener(_hwnd); } catch { }
        try
        {
            if (_proc is not null) NativeMethods.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        }
        catch { }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static IntPtr SubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            try
            {
                var ptr = (IntPtr)(nint)(nuint)(ulong)dwRefData;
                if (ptr != IntPtr.Zero && GCHandle.FromIntPtr(ptr).Target is ClipboardMonitorService self)
                    self.ClipboardChanged?.Invoke(self, EventArgs.Empty);
            }
            catch { }
        }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
