using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>
/// Finds the window behind a loopback HTTP client: the TCP table maps the client's port to the process that
/// owns the connection (e.g. curl run by a Claude Code hook), then the parent chain is walked to the first
/// ancestor with a top-level window (Windows Terminal, VS Code, ...). Parent creation times are checked so a
/// recycled parent PID is never trusted.
/// </summary>
internal static class ClientProcessResolver
{
    private const int TcpTableOwnerPidConnections = 4;
    private static readonly HashSet<string> NotHosts = new(StringComparer.OrdinalIgnoreCase) { "explorer", "svchost", "services", "wininit", "csrss", "MyNotch" };

    internal static (int? Pid, long? Hwnd) Resolve(int clientPort, int serverPort)
    {
        try
        {
            int? pid = OwnerOf(clientPort, serverPort);
            if (pid is null) return (null, null);
            int current = pid.Value;
            ulong childCreated = DevProcessInspector.ParentOf(current)?.Created ?? ulong.MaxValue;
            for (int depth = 0; depth < 10; depth++)
            {
                if (DevProcessInspector.ParentOf(current) is not { } info) break;
                int parent = info.Parent;
                if (parent <= 4 || DevProcessInspector.ParentOf(parent) is not { } parentInfo || parentInfo.Created > childCreated) break;
                try
                {
                    using var process = Process.GetProcessById(parent);
                    if (!NotHosts.Contains(process.ProcessName) && process.MainWindowHandle != IntPtr.Zero)
                        return (parent, process.MainWindowHandle.ToInt64());
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { break; }
                current = parent;
                childCreated = parentInfo.Created;
            }
            return (pid, null);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException) { return (null, null); }
    }

    /// <summary>Owner of the IPv4 connection 127.0.0.1:clientPort -> 127.0.0.1:serverPort.</summary>
    private static int? OwnerOf(int clientPort, int serverPort)
    {
        uint size = 0;
        _ = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, NativeMethods.AF_INET, TcpTableOwnerPidConnections, 0);
        if (size == 0 || size > 16 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AF_INET, TcpTableOwnerPidConnections, 0) != NativeMethods.NO_ERROR) return null;
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
}
