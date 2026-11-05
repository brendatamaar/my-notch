using System.Runtime.InteropServices;
using MyNotch.Interop;

namespace MyNotch.Services;

/// <summary>
/// Event-driven watcher for the active window and fullscreen apps (AGENTS.md: no polling).
/// Sources: WinEvent hooks for foreground / move-size-end / restore, plus the shell's appbar
/// ABN_FULLSCREENAPP notification, which also covers F11 and video fullscreen without a focus change.
/// Every callback arrives on the UI thread that called <see cref="Start"/>.
/// </summary>
public sealed class DisplayWatcher : IDisposable
{
    private const uint AppBarMessage = NativeMethods.WM_APP + 7;
    private static readonly UIntPtr SubclassId = new(0x44697370); // "Disp"
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly List<IntPtr> _hooks = new();
    private NativeMethods.WinEventProc? _winEvent;
    private NativeMethods.SubclassProc? _subclass;
    private GCHandle _self;
    private IntPtr _hwnd;
    private bool _appBar, _started, _disposed;

    /// <summary>A real application window became active (not the shell, not MyNotch).</summary>
    public event EventHandler<IntPtr>? ForegroundChanged;
    /// <summary>Any window, shell included, became active. Another always-on-top window may now sit above MyNotch.</summary>
    public event EventHandler? ActivationChanged;
    /// <summary>A fullscreen app started or stopped covering the monitor MyNotch is on.</summary>
    public event EventHandler<bool>? FullscreenChanged;

    public bool IsFullscreen { get; private set; }

    public void Start(IntPtr hwnd)
    {
        if (_started || _disposed || hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;
        _winEvent = OnWinEvent;
        foreach (uint e in new[] { NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_MOVESIZEEND, NativeMethods.EVENT_SYSTEM_MINIMIZEEND, NativeMethods.EVENT_OBJECT_SHOW })
        {
            var hook = NativeMethods.SetWinEventHook(e, e, IntPtr.Zero, _winEvent, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }

        _subclass = SubclassCallback;
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        if (NativeMethods.SetWindowSubclass(hwnd, _subclass, SubclassId, (UIntPtr)(nuint)(nint)GCHandle.ToIntPtr(_self)))
        {
            var data = new NativeMethods.APPBARDATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(), hWnd = hwnd, uCallbackMessage = AppBarMessage };
            // ABM_NEW without ABM_SETPOS reserves no screen space; it only subscribes to shell notifications.
            _appBar = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data) != UIntPtr.Zero;
        }
        _started = true;
        Evaluate();
    }

    /// <summary>Re-checks fullscreen state now (e.g. after MyNotch moved to another monitor).</summary>
    public void Evaluate()
    {
        if (!_started || _disposed) return;
        bool full = ForegroundIsFullscreen();
        if (full == IsFullscreen) return;
        IsFullscreen = full;
        FullscreenChanged?.Invoke(this, full);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || idObject != 0 || idChild != 0) return; // OBJID_WINDOW / CHILDID_SELF only
        try
        {
            if (eventType == NativeMethods.EVENT_OBJECT_SHOW)
            {
                // Shown without activation (video pop-outs, overlays): only a topmost window over the notch matters.
                if (CoversNotchFromAbove(hwnd)) ActivationChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND && IsAppWindow(hwnd)) ForegroundChanged?.Invoke(this, hwnd);
            Evaluate();
            if (eventType is NativeMethods.EVENT_SYSTEM_FOREGROUND or NativeMethods.EVENT_SYSTEM_MINIMIZEEND) ActivationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MyNotch] Display watcher: {ex.Message}"); }
    }

    private bool IsAppWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        return root != _hwnd && NativeMethods.IsWindowVisible(root) && !ShellClasses.Contains(NativeMethods.ClassName(root));
    }

    private bool CoversNotchFromAbove(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _hwnd || NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return false;
        if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOPMOST) == 0) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var other) || !NativeMethods.GetWindowRect(_hwnd, out var notch)) return false;
        return other.Left < notch.Right && notch.Left < other.Right && other.Top < notch.Bottom && notch.Top < other.Bottom;
    }

    private bool ForegroundIsFullscreen()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (!IsAppWindow(foreground) || NativeMethods.IsIconic(foreground)) return false;
        var monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)) return false;

        // Exclusive D3D fullscreen and presentation mode (PowerPoint slideshow) report themselves.
        if (NativeMethods.SHQueryUserNotificationState(out int quns) == 0 && quns is NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE)
            return true;

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info) || !NativeMethods.GetWindowRect(foreground, out var rect)) return false;
        bool covers = rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top && rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom;
        if (!covers) return false;
        // A maximized window covers the monitor when the taskbar auto-hides; that isn't fullscreen. This matters for
        // frameless apps (browsers, VS Code, Spotify...), which have no title bar to rule them out below.
        if (NativeMethods.IsZoomed(foreground)) return false;
        // A window with a title bar sized over the whole monitor isn't fullscreen either.
        long style = NativeMethods.GetWindowLongPtr(foreground, NativeMethods.GWL_STYLE).ToInt64();
        return (style & NativeMethods.WS_CAPTION) != NativeMethods.WS_CAPTION;
    }

    private static IntPtr SubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == AppBarMessage && (int)wParam == NativeMethods.ABN_FULLSCREENAPP)
        {
            try
            {
                var ptr = (IntPtr)(nint)(nuint)(ulong)dwRefData;
                if (ptr != IntPtr.Zero && GCHandle.FromIntPtr(ptr).Target is DisplayWatcher self) self.Evaluate();
            }
            catch { }
        }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        if (_appBar)
        {
            var data = new NativeMethods.APPBARDATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(), hWnd = _hwnd };
            _ = NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        }
        try { if (_subclass is not null && _hwnd != IntPtr.Zero) NativeMethods.RemoveWindowSubclass(_hwnd, _subclass, SubclassId); } catch { }
        if (_self.IsAllocated) _self.Free();
        _winEvent = null;
    }
}
