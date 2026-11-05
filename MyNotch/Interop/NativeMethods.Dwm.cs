using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>DWM window attributes (LibraryImport, AOT-safe).</summary>
internal static partial class NativeMethods
{
    internal const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const uint DWMWA_BORDER_COLOR = 34;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, uint size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}
