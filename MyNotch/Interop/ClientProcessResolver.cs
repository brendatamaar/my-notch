using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>
/// Finds the window behind a loopback HTTP client: the TCP table maps the client's port to the process that
/// owns the connection (e.g. curl run by a Claude Code hook), then the parent chain is walked to the first
/// ancestor with a top-level window (Windows Terminal, VS Code, ...). Parent creation times are checked so a
/// recycled parent PID is never trusted.
/// </summary>
/// <remarks>
/// Claude Code runs a hook (a fresh curl) on every tool call, and the walk asks each ancestor for its main window,
/// which enumerates every top-level window. The answer is cached per launching process (curl's parent, identified
/// by pid and creation time) for a short while, so a busy session costs one table lookup per hook.
/// </remarks>
internal static partial class ClientProcessResolver
{
    private const int TcpTableOwnerPidConnections = 4;
    private const uint ErrorInsufficientBuffer = 122;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> NotHosts = new(StringComparer.OrdinalIgnoreCase) { "explorer", "svchost", "services", "wininit", "csrss", "MyNotch" };
    private static readonly Dictionary<(int Pid, ulong Created), (long At, int Pid, long? Hwnd)> Cache = new();

    internal static (int? Pid, long? Hwnd) Resolve(int clientPort, int serverPort)
    {
        try
        {
            int? pid = OwnerOf(clientPort, serverPort);
            if (pid is null) return (null, null);
            if (DevProcessInspector.ParentOf(pid.Value) is not { } self) return (pid, null);

            int launcher = self.Parent;
            ulong childCreated = self.Created;
            var launcherInfo = DevProcessInspector.ParentOf(launcher);
            var key = (launcher, launcherInfo?.Created ?? 0);
            if (launcherInfo is not null && TryCached(key, out var cached)) return cached;

            (int? Pid, long? Hwnd) result = (pid, null);
            var parentInfo = launcherInfo;
            int parent = launcher;
            for (int depth = 0; depth < 10; depth++)
            {
                if (parent <= 4 || parentInfo is not { } info || info.Created > childCreated) break;
                try
                {
                    using var process = Process.GetProcessById(parent);
                    if (!NotHosts.Contains(process.ProcessName) && process.MainWindowHandle != IntPtr.Zero)
                    {
                        result = (parent, process.MainWindowHandle.ToInt64());
                        break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { break; }
                childCreated = info.Created;
                parent = info.Parent;
                parentInfo = DevProcessInspector.ParentOf(parent);
            }
            // Only a found window is worth remembering; "no window" is cheap to recompute and may change.
            if (launcherInfo is not null && result.Hwnd is not null) Remember(key, result.Pid!.Value, result.Hwnd);
            return result;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException) { return (null, null); }
    }

    private static bool TryCached((int, ulong) key, out (int? Pid, long? Hwnd) result)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var entry) && Stopwatch.GetElapsedTime(entry.At) < CacheLifetime
                && entry.Hwnd is { } hwnd && IsWindow(new IntPtr(hwnd)))
            {
                result = (entry.Pid, entry.Hwnd);
                return true;
            }
            Cache.Remove(key);
        }
        result = default;
        return false;
    }

    private static void Remember((int, ulong) key, int pid, long? hwnd)
    {
        lock (Cache)
        {
            if (Cache.Count >= 32) Cache.Clear();
            Cache[key] = (Stopwatch.GetTimestamp(), pid, hwnd);
        }
    }

    /// <summary>Owner of the IPv4 connection 127.0.0.1:clientPort -> 127.0.0.1:serverPort.</summary>
    private static int? OwnerOf(int clientPort, int serverPort)
    {
        uint size = 0;
        _ = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, NativeMethods.AF_INET, TcpTableOwnerPidConnections, 0);
        // The table can grow between the size probe and the read; retry with the size Windows reports.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size == 0 || size > 16 * 1024 * 1024) return null;
            size += 24 * 16; // headroom for connections opened meanwhile
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint status = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AF_INET, TcpTableOwnerPidConnections, 0);
                if (status == ErrorInsufficientBuffer) continue;
                if (status != NativeMethods.NO_ERROR) return null;
                int count = Math.Min(Marshal.ReadInt32(buffer), Math.Max(0, ((int)size - 4) / 24));
                for (int i = 0; i < count; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (24 bytes).
                    IntPtr row = buffer + 4 + i * 24;
                    int local = Services.PortMonitorService.NetworkOrderToPort(Marshal.ReadInt32(row, 8));
                    int remote = Services.PortMonitorService.NetworkOrderToPort(Marshal.ReadInt32(row, 16));
                    if (local == clientPort && remote == serverPort) return Marshal.ReadInt32(row, 20);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);
}
