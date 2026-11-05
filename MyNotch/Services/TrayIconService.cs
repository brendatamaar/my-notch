using System.Runtime.InteropServices;
using MyNotch.Interop;

namespace MyNotch.Services;

/// <summary>
/// Notification-area icon + native right-click menu. Event driven: the shell posts a
/// callback message that arrives through a comctl32 window subclass on the UI thread,
/// chaining with the clipboard and hotkey subclasses (own slot id). The same menu is
/// shown when the notch itself is right-clicked.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_APP + 1;
    private const uint IconId = 1;
    private const int CmdOpen = 1;
    private const int CmdPalette = 2;
    private const int CmdNotifications = 3;
    private const int CmdSettings = 4;
    private const int CmdStartup = 5;
    private const int CmdQuit = 9;
    private static readonly UIntPtr SubclassId = new(0x54726179); // "Tray"

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _icon = IntPtr.Zero;
    private NativeMethods.SubclassProc? _proc;
    private GCHandle _selfHandle;
    private uint _taskbarCreated;
    private bool _started;
    private bool _disposed;

    /// <summary>Informational, disabled menu line (e.g. the webhook address).</summary>
    public string InfoLine { get; set; } = string.Empty;

    public event EventHandler? OpenRequested;
    public event EventHandler? PaletteRequested;
    public event EventHandler? NotificationSettingsRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? StartupToggleRequested;
    public event EventHandler? QuitRequested;

    /// <summary>Supplies the "Start with Windows" item's label and check mark each time the menu opens.</summary>
    public Func<(string Label, bool Checked)>? StartupItem { get; set; }

    public bool Start(IntPtr hwnd)
    {
        if (_disposed || _started) return _started;
        if (hwnd == IntPtr.Zero) return false;

        _hwnd = hwnd;
        _proc = SubclassCallback;
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var refData = (UIntPtr)(nuint)(nint)GCHandle.ToIntPtr(_selfHandle);

        if (!NativeMethods.SetWindowSubclass(_hwnd, _proc, SubclassId, refData))
        {
            _selfHandle.Free();
            _proc = null;
            return false;
        }

        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _icon = TrayIconArt.Create();
        _started = AddIcon();
        return _started;
    }

    /// <summary>Shows the native menu at the cursor and raises the matching event.</summary>
    public void ShowMenu()
    {
        if (!_started) return;

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        int command;
        try
        {
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdOpen, "Open notch");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdPalette, "Actions");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdSettings, "Settings");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdNotifications, "Notification settings");
            if (StartupItem?.Invoke() is { } startup)
            {
                _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
                _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | (startup.Checked ? NativeMethods.MF_CHECKED : 0), CmdStartup, startup.Label);
            }
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            if (!string.IsNullOrEmpty(InfoLine))
                _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, InfoLine);
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CmdQuit, "Quit MyNotch");

            _ = NativeMethods.GetCursorPos(out var pt);
            // The menu only dismisses on an outside click if the owner is foreground.
            _ = NativeMethods.SetForegroundWindow(_hwnd);
            command = NativeMethods.TrackPopupMenuEx(
                menu, NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            _ = NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }

        switch (command)
        {
            case CmdOpen: OpenRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdPalette: PaletteRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdNotifications: NotificationSettingsRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdSettings: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdStartup: StartupToggleRequested?.Invoke(this, EventArgs.Empty); break;
            case CmdQuit: QuitRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            _started = false;
            RemoveIcon();
            try
            {
                if (_proc is not null) NativeMethods.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            }
            catch { }
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        if (_icon != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _proc = null;
        _hwnd = IntPtr.Zero;
    }

    private unsafe NativeMethods.NOTIFYICONDATAW BuildData()
    {
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        const string tip = "MyNotch";
        for (int i = 0; i < tip.Length; i++) data.szTip[i] = tip[i];
        return data;
    }

    private bool AddIcon()
    {
        var data = BuildData();
        return NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    private void RemoveIcon()
    {
        var data = BuildData();
        _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
    }

    private static IntPtr SubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        try
        {
            var ptr = (IntPtr)(nint)(nuint)(ulong)dwRefData;
            if (ptr != IntPtr.Zero && GCHandle.FromIntPtr(ptr).Target is TrayIconService self)
            {
                if (uMsg == CallbackMessage)
                {
                    var mouse = (uint)((long)lParam & 0xFFFF);
                    if (mouse == NativeMethods.WM_RBUTTONUP) self.ShowMenu();
                    else if (mouse == NativeMethods.WM_LBUTTONUP) self.OpenRequested?.Invoke(self, EventArgs.Empty);
                }
                else if (self._taskbarCreated != 0 && uMsg == self._taskbarCreated)
                {
                    // Explorer restarted: its tray was rebuilt without us.
                    self.AddIcon();
                }
            }
        }
        catch { }
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}

/// <summary>Draws the tray icon in code (orange tile, black island on top) so no asset file is needed.</summary>
internal static class TrayIconArt
{
    private const int Size = 32;

    internal static unsafe IntPtr Create()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double tile = Coverage(x, y, 1, 1, 30, 30, 8);          // orange tile
                double island = Coverage(x, y, 8, 3, 16, 7, 3.5);        // black island
                // BGRA, straight alpha over transparent.
                double a = tile;
                double r = 240, g = 137, b = 46;                          // #F0892E
                if (island > 0)
                {
                    r = r * (1 - island);
                    g = g * (1 - island);
                    b = b * (1 - island);
                }
                int i = (y * Size + x) * 4;
                pixels[i + 0] = (byte)b;
                pixels[i + 1] = (byte)g;
                pixels[i + 2] = (byte)r;
                pixels[i + 3] = (byte)(a * 255);
            }
        }

        var mask = new byte[Size * Size / 8]; // all zero: colour bitmap's alpha decides
        IntPtr color, maskBmp;
        fixed (byte* p = pixels) color = NativeMethods.CreateBitmap(Size, Size, 1, 32, (IntPtr)p);
        fixed (byte* m = mask) maskBmp = NativeMethods.CreateBitmap(Size, Size, 1, 1, (IntPtr)m);
        if (color == IntPtr.Zero || maskBmp == IntPtr.Zero)
        {
            if (color != IntPtr.Zero) NativeMethods.DeleteObject(color);
            if (maskBmp != IntPtr.Zero) NativeMethods.DeleteObject(maskBmp);
            return IntPtr.Zero;
        }

        var info = new NativeMethods.ICONINFO { fIcon = 1, hbmColor = color, hbmMask = maskBmp };
        var icon = NativeMethods.CreateIconIndirect(ref info);
        NativeMethods.DeleteObject(color);
        NativeMethods.DeleteObject(maskBmp);
        return icon;
    }

    /// <summary>1px anti-aliased rounded-rectangle coverage for the pixel centre (x+.5, y+.5).</summary>
    private static double Coverage(int px, int py, double x, double y, double w, double h, double radius)
    {
        double cx = x + w / 2, cy = y + h / 2;
        double dx = Math.Abs(px + 0.5 - cx) - (w / 2 - radius);
        double dy = Math.Abs(py + 0.5 - cy) - (h / 2 - radius);
        double outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0));
        double dist = outside + Math.Min(Math.Max(dx, dy), 0) - radius;
        return Math.Clamp(0.5 - dist, 0, 1);
    }
}
