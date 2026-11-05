using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MyNotch.Interop;

internal sealed record LocalProcessDetails(string Name, string Executable, string CommandLine, string WorkingDirectory, ulong Created);

/// <summary>Read only a process's image, command line and current-directory descriptor, never its environment.</summary>
internal static partial class DevProcessInspector
{
    internal static LocalProcessDetails? Read(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            if (process.SessionId == 0 || process.SessionId != self.SessionId) return null;
            IntPtr handle = OpenProcess(0x1010, false, pid);
            if (handle == IntPtr.Zero) handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                string executable = Image(handle);
                if (executable.Length == 0 || !GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
                return new(process.ProcessName, executable, Command(handle), CurrentDirectory(handle), created);
            }
            finally { CloseHandle(handle); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException) { return null; }
    }
    private static unsafe string Image(IntPtr process)
    {
        char[] buffer = new char[32768]; uint length = (uint)buffer.Length;
        fixed (char* chars = buffer) return QueryFullProcessImageName(process, 0, chars, ref length) ? new string(chars, 0, (int)length) : "";
    }
    private static string Command(IntPtr process)
    {
        NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out var length);
        if (length < 8 || length > 128 * 1024) return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (NtQueryInformationProcess(process, 60, buffer, length, out _) < 0) return "";
            int bytes = (ushort)Marshal.ReadInt16(buffer);
            IntPtr chars = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            long offset = chars.ToInt64() - buffer.ToInt64();
            if (bytes % 2 != 0 || offset < (IntPtr.Size == 8 ? 16 : 8) || offset + bytes > length) return "";
            return Marshal.PtrToStringUni(chars, bytes / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string CurrentDirectory(IntPtr process)
    {
        int pointers = IntPtr.Size;
        IntPtr data = Marshal.AllocHGlobal(48);
        try
        {
            if (NtQueryInformationProcess(process, 0, data, 48, out _) < 0) return "";
            ulong peb = (ulong)Marshal.ReadIntPtr(data, IntPtr.Size).ToInt64();
            if (IntPtr.Size == 8 && NtQueryInformationProcess(process, 26, data, 8, out _) >= 0 && Marshal.ReadInt64(data) != 0)
            { peb = (ulong)Marshal.ReadInt64(data); pointers = 4; }
            var parameters = ReadBytes(process, peb + (pointers == 8 ? 0x20u : 0x10u), pointers);
            if (parameters is null) return "";
            ulong address = pointers == 8 ? BitConverter.ToUInt64(parameters) : BitConverter.ToUInt32(parameters);
            var directory = ReadBytes(process, address + (pointers == 8 ? 0x38u : 0x24u), pointers == 8 ? 16 : 8);
            if (directory is null) return "";
            int length = BitConverter.ToUInt16(directory);
            ulong text = pointers == 8 ? BitConverter.ToUInt64(directory, 8) : BitConverter.ToUInt32(directory, 4);
            if (length <= 0 || length > 65534 || length % 2 != 0 || text == 0) return "";
            var bytes = ReadBytes(process, text, length);
            return bytes is null ? "" : Encoding.Unicode.GetString(bytes);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static unsafe byte[]? ReadBytes(IntPtr process, ulong address, int length)
    {
        if (address == 0) return null;
        var bytes = new byte[length];
        fixed (byte* buffer = bytes)
            return ReadProcessMemory(process, (IntPtr)address, (IntPtr)buffer, (nuint)length, out var read) && read == (nuint)length ? bytes : null;
    }
    /// <summary>Parent pid and this process's creation time (FILETIME), or null when the process can't be opened.</summary>
    internal static (int Parent, ulong Created)? ParentOf(int pid)
    {
        if (pid <= 4) return null;
        IntPtr handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return null;
        IntPtr data = Marshal.AllocHGlobal(IntPtr.Size * 6);
        try
        {
            // PROCESS_BASIC_INFORMATION: ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId.
            if (NtQueryInformationProcess(handle, 0, data, (uint)(IntPtr.Size * 6), out _) < 0) return null;
            if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            return ((int)Marshal.ReadIntPtr(data, IntPtr.Size * 5).ToInt64(), created);
        }
        finally { Marshal.FreeHGlobal(data); CloseHandle(handle); }
    }

    internal static bool Stop(int pid, ulong created, out string error)
    {
        error = "";
        IntPtr process = OpenProcess(0x1001, false, pid);
        if (process == IntPtr.Zero) { error = "Process exited or cannot be stopped."; return false; }
        try
        {
            if (!GetProcessTimes(process, out var actual, out _, out _, out _) || actual != created)
            { error = "Process changed. Refresh the server list."; return false; }
            if (TerminateProcess(process, 1)) return true;
            error = "Windows could not stop this server."; return false;
        }
        finally { CloseHandle(process); }
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(IntPtr process, uint flags, char* name, ref uint length);
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, uint length, out uint returned);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint read);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(IntPtr process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr process, uint exitCode);
}
