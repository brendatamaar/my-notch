using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>Win32 interop via LibraryImport (AOT-safe, per AGENTS.md — no DllImport).</summary>
internal static partial class NativeMethods
{
    internal const uint WDA_NONE = 0x00000000;
    internal const uint WDA_MONITOR = 0x00000001;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_CAPTION = 0x00C00000L;
    internal const long WS_THICKFRAME = 0x00040000L;
    internal const long WS_SYSMENU = 0x00080000L;
    internal const long WS_EX_LAYERED = 0x00080000L;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal const uint WM_CLIPBOARDUPDATE = 0x031D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    // Window subclassing (comctl32) so the WinUI 3 HWND receives WM_CLIPBOARDUPDATE.
    // Delegate instance must be kept alive for the lifetime of the subclass.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [LibraryImport("comctl32.dll")]
    internal static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    internal const uint WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint VK_SPACE = 0x20;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const int AF_INET = 2;
    internal const int AF_INET6 = 23;
    internal const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    internal const uint NO_ERROR = 0;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;

    // Parses raw MIB_TCPTABLE_OWNER_PID / MIB_TCP6TABLE_OWNER_PID bytes in PortMonitorService.
    // IPv4 row = 24 bytes (port DWORD at +8, pid DWORD at +20); IPv6 row = 56 bytes
    // (port DWORD at +20, pid DWORD at +52). Ports are network-byte-order in the low 16 bits.
    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    internal static partial uint GetExtendedTcpTable(IntPtr pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);

    internal const int SW_RESTORE = 9;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary>On success the system owns <paramref name="hRgn"/>; on failure the caller must delete it.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);
}
