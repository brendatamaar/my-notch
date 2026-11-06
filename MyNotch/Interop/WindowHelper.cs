using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace MyNotch.Interop;

/// <summary>Applies the Phase 1 "invisible shell": frameless, transparent, always-on-top, capture-excluded.</summary>
internal static class WindowHelper
{
    // Bleed one physical pixel past the monitor edge so the WinUI/DWM top seam
    // is off-screen. Keep this in pixels: scaling it as a DIP over-corrects at high DPI.
    private const int TopEdgeBleedPixels = 1;

    internal static IntPtr GetHwnd(Window window) => WindowNative.GetWindowHandle(window);

    /// <summary>
    /// Configures the borderless shell. <paramref name="sizeDip"/> is in device-independent
    /// pixels, so the notch looks the same at 100% and 175% display scaling.
    /// </summary>
    internal static void ConfigureNotchShell(Window window, SizeInt32 sizeDip)
    {
        var hwnd = GetHwnd(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        window.ExtendsContentIntoTitleBar = true;
        window.SystemBackdrop = new TransparentBackdrop();

        // Windows 11 rounds every top-level window and draws a 1px border; the island has its own shape.
        int noRound = NativeMethods.DWMWCP_DONOTROUND;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        uint noBorder = NativeMethods.DWMWA_COLOR_NONE;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));


        StripChrome(hwnd);
        SetAlwaysOnTop(hwnd);
        ExcludeFromCapture(hwnd);
        CenterOnTop(window, appWindow, sizeDip);
    }

    /// <summary>The display the notch sits on; null keeps whichever display the window is on (primary at launch).</summary>
    internal static DisplayId? TargetDisplay { get; set; }

    /// <summary>Centres the window on the target display's top edge (flush with the screen, not the work area).</summary>
    internal static void CenterOnTop(Window window, AppWindow appWindow, SizeInt32 sizeDip)
    {
        var hwnd = GetHwnd(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        DisplayArea? display = null;
        if (TargetDisplay is { } target)
        {
            try { display = DisplayArea.GetFromDisplayId(target); } catch (Exception ex) when (ex is ArgumentException or COMException) { }
        }
        display ??= DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var bounds = display.OuterBounds;
        // Size with the destination monitor's DPI so a move between 100% and 150% screens lands at the right size.
        var monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        var size = monitor != IntPtr.Zero && NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0
            ? Scale(sizeDip, dpi)
            : ToPixels(hwnd, sizeDip);
        var x = bounds.X + (bounds.Width - size.Width) / 2;
        appWindow.MoveAndResize(new RectInt32(x, bounds.Y - TopEdgeBleedPixels, size.Width, size.Height));
    }

    internal static void Resize(Window window, SizeInt32 sizeDip)
    {
        var hwnd = GetHwnd(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        CenterOnTop(window, appWindow, sizeDip);
    }

    /// <summary>
    /// Clips the (fixed-size) window to a top-centred <paramref name="stageDip"/> rectangle. Outside it the window
    /// neither draws nor takes the pointer, so clicks fall through to whatever is underneath. Changing the region
    /// never resizes or moves the window, so the XAML content can't jump for a frame the way a resize does.
    /// </summary>
    internal static void ClipToStage(Window window, SizeInt32 windowDip, SizeInt32 stageDip)
    {
        var hwnd = GetHwnd(window);
        var win = ToPixels(hwnd, windowDip);
        var stage = ToPixels(hwnd, stageDip);
        int width = Math.Min(stage.Width, win.Width);
        int left = (win.Width - width) / 2;
        int bottom = Math.Min(stage.Height, win.Height);
        var region = NativeMethods.CreateRectRgn(left, 0, left + width, bottom);
        if (region == IntPtr.Zero) return;
        if (NativeMethods.SetWindowRgn(hwnd, region, true) == 0) _ = NativeMethods.DeleteObject(region);
    }

    private static SizeInt32 ToPixels(IntPtr hwnd, SizeInt32 dip) => Scale(dip, NativeMethods.GetDpiForWindow(hwnd));

    private static SizeInt32 Scale(SizeInt32 dip, uint dpi)
    {
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        return new SizeInt32((int)Math.Round(dip.Width * scale), (int)Math.Round(dip.Height * scale));
    }

    /// <summary>Displays for the settings picker: id, label, is-primary.</summary>
    internal static IReadOnlyList<(DisplayId Id, string Label, bool Primary)> Displays()
    {
        var result = new List<(DisplayId, string, bool)>();
        try
        {
            var primary = DisplayArea.Primary.DisplayId.Value;
            var all = DisplayArea.FindAll();
            for (int i = 0; i < all.Count; i++)
            {
                var area = all[i];
                bool isPrimary = area.DisplayId.Value == primary;
                result.Add((area.DisplayId, $"Display {i + 1} · {area.OuterBounds.Width}×{area.OuterBounds.Height}{(isPrimary ? " (primary)" : "")}", isPrimary));
            }
        }
        catch (COMException) { }
        return result;
    }

    /// <summary>The display containing most of <paramref name="hwnd"/>.</summary>
    internal static DisplayId? DisplayOf(IntPtr hwnd)
    {
        try { return DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Nearest)?.DisplayId; }
        catch (Exception ex) when (ex is ArgumentException or COMException) { return null; }
    }

    internal static void Hide(Window window) => _ = NativeMethods.ShowWindow(GetHwnd(window), NativeMethods.SW_HIDE);

    internal static void ShowWithoutActivating(Window window)
    {
        var hwnd = GetHwnd(window);
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        SetAlwaysOnTop(hwnd);
        ExcludeFromCapture(hwnd);
    }

    private static void StripChrome(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU);
        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        // WS_EX_LAYERED lets the transparent XAML surface composite over the desktop;
        // WS_EX_TOOLWINDOW keeps the notch out of Alt-Tab / taskbar.
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;
        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Moves the notch back to the top of the always-on-top band. Another topmost window (a video pop-out, a
    /// sticky-note app, a meeting toolbar) that activates later is placed above it, so this runs on every
    /// activation change. Start, Search and other system shell surfaces live in a higher band and still cover it.
    /// </summary>
    internal static void KeepOnTop(Window window)
    {
        var hwnd = GetHwnd(window);
        if (NativeMethods.IsWindowVisible(hwnd)) SetAlwaysOnTop(hwnd);
    }

    private static void SetAlwaysOnTop(IntPtr hwnd) =>
        _ = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>Bring a webhook sender's window to the foreground (PRD: clicking an alert focuses the agent).</summary>
    internal static bool FocusSender(long? hwndValue, int? pid)
    {
        var hwnd = IntPtr.Zero;
        if (hwndValue is > 0) hwnd = new IntPtr(hwndValue.Value);
        else if (pid is > 0)
        {
            try { using var process = System.Diagnostics.Process.GetProcessById(pid.Value); hwnd = process.MainWindowHandle; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        if (hwnd == IntPtr.Zero) return false;
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        return NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>WDA_EXCLUDEFROMCAPTURE: invisible to Teams/Zoom/Meet and screen recording (PRD §3.1).</summary>
    internal static bool ExcludeFromCapture(IntPtr hwnd)
    {
#if DEBUG
        // Debug builds only: lets automated screenshots see the notch. Release always excludes.
        if (Environment.GetEnvironmentVariable("MYNOTCH_ALLOW_CAPTURE") == "1")
            return NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE);
#endif
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            return true;
        System.Diagnostics.Debug.WriteLine(
            $"[MyNotch] SetWindowDisplayAffinity failed: {Marshal.GetLastPInvokeErrorMessage()}");
        return false;
    }
}

