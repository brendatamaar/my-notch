using System.Runtime.InteropServices;
using MyNotch.Interop;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// On-demand listening-port enumeration via GetExtendedTcpTable (PLAN Phase 3, PRD §3.3).
/// No polling loop: callers refresh explicitly (e.g. when the notch expands).
/// All methods are thread-safe and return detached lists; UI updates stay on the caller.
/// </summary>
public sealed class PortMonitorService
{
    public IReadOnlyList<ListeningPortInfo> Refresh()
    {
        var raw = ReadFamily(NativeMethods.AF_INET, "TCPv4");
        raw.AddRange(ReadFamily(NativeMethods.AF_INET6, "TCPv6"));
        var result = new List<ListeningPortInfo>();
        var identities = new Dictionary<int, DevServerIdentity?>();
        foreach (var endpoint in raw)
        {
            if (!identities.TryGetValue(endpoint.Pid, out var identity))
            {
                identity = DevServerResolver.Resolve(endpoint.Pid);
                identities[endpoint.Pid] = identity;
            }
            if (identity is null || DevServerFilter.DebuggerPort(identity.CommandLine, endpoint.Port)) continue;
            result.Add(endpoint with { ProcessName = identity.Name, ProjectPath = identity.ProjectRoot, Framework = identity.Framework, Created = identity.Created, CommandLine = identity.CommandLine, WorkingDirectory = identity.WorkingDirectory });
        }
        return result.GroupBy(p => (p.Port, p.Pid)).Select(g => g.First()).OrderBy(p => p.Port).ThenBy(p => p.Pid).ToArray();
    }

    public bool TryStop(ListeningPortInfo selected, out string error)
    {
        error = "";
        if (selected.Created == 0 || !Refresh().Any(p => p.Pid == selected.Pid && p.Port == selected.Port && p.Created == selected.Created))
        { error = "Server changed or is no longer running. Refresh the list."; return false; }
        return DevProcessInspector.Stop(selected.Pid, selected.Created, out error);
    }

    /// <summary>
    /// Stops the server, waits for it to exit, then reruns its exact command line in a new console window in its
    /// original working directory. The new window shows the server's output and can be closed to stop it.
    /// </summary>
    public bool TryRestart(ListeningPortInfo selected, out string error)
    {
        string command = selected.CommandLine.Trim();
        string directory = Directory.Exists(selected.WorkingDirectory) ? selected.WorkingDirectory : selected.ProjectPath;
        if (command.Length == 0 || !Directory.Exists(directory)) { error = "This server's command line is unknown; restart it from its terminal."; return false; }
        if (!TryStop(selected, out error)) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(selected.Pid);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try
        {
            // cmd /s /k "<line>" keeps the line verbatim; metacharacters outside quotes are caret-escaped so
            // the original command can't be split into several commands.
            string title = EscapeForCmd(Path.GetFileName(selected.ProjectPath.TrimEnd('\\', '/')));
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/s /k \"title {title} & {EscapeForCmd(command)}\"",
                WorkingDirectory = directory,
                UseShellExecute = true,
            };
            using var started = System.Diagnostics.Process.Start(start);
            if (started is null) { error = "The new terminal didn't start."; return false; }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = "Stopped, but relaunch failed: " + ex.Message;
            return false;
        }
    }

    /// <summary>Caret-escapes cmd.exe metacharacters that appear outside double quotes.</summary>
    internal static string EscapeForCmd(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 8);
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '&' or '|' or '<' or '>' or '^' or '(' or ')') sb.Append('^');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<ListeningPortInfo> ReadFamily(int family, string protocol)
    {
        var list = new List<ListeningPortInfo>();
        uint size = 0;

        // Size probe: expect ERROR_INSUFFICIENT_BUFFER with the required size.
        _ = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, bOrder: true,
            family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size == 0)
            return list;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint ret = NativeMethods.GetExtendedTcpTable(buffer, ref size, bOrder: true,
                family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (ret != NativeMethods.NO_ERROR)
                return list;

            int count = Marshal.ReadInt32(buffer, 0);
            if (count <= 0)
                return list;

            bool v4 = family == NativeMethods.AF_INET;
            int rowSize = v4 ? 24 : 56;
            int portOffset = v4 ? 8 : 20;
            int pidOffset = v4 ? 20 : 52;

            count = Math.Min(count, Math.Max(0, ((int)size - 4) / rowSize));
            long baseAddr = buffer.ToInt64() + 4;
            for (int i = 0; i < count; i++)
            {
                long row = baseAddr + (long)i * rowSize;
                int portRaw = Marshal.ReadInt32(new IntPtr(row + portOffset));
                int pid = Marshal.ReadInt32(new IntPtr(row + pidOffset));
                int port = NetworkOrderToPort(portRaw);
                if (port is <= 0 or > 65535 || pid <= 0)
                    continue;
                byte[] addressBytes = new byte[v4 ? 4 : 16];
                Marshal.Copy(new IntPtr(row + (v4 ? 4 : 0)), addressBytes, 0, addressBytes.Length);
                string address = new System.Net.IPAddress(addressBytes).ToString();
                if (!DevServerFilter.LocalEndpoint(address)) continue;
                list.Add(new ListeningPortInfo
                {
                    Port = port,
                    Pid = pid,
                    Address = address,
                    Protocol = protocol,
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    // Port sits in the low 16 bits, network byte order: swap the two bytes.
    internal static int NetworkOrderToPort(int raw)
    {
        int lo = raw & 0xFFFF;
        return ((lo >> 8) & 0xFF) | ((lo << 8) & 0xFF00);
    }

}
