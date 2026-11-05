using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MyNotch.Services;

public enum StartupState { Off, On, OtherCopy, DisabledInTaskManager }

/// <summary>
/// "Start with Windows" via the per-user Run key (no admin rights). Task Manager's Startup apps page records
/// its own enable/disable switch under StartupApproved\Run; enabling here clears a Task Manager "disabled".
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "MyNotch";

    public static string? ExecutablePath => Environment.ProcessPath;

    public static (StartupState State, string? RegisteredPath) Read()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string command || command.Trim().Length == 0) return (StartupState.Off, null);
            string path = ExecutableFromCommand(command);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte 0x02/0x06 = enabled; odd values (0x03) = disabled by the user in Task Manager.
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1) return (StartupState.DisabledInTaskManager, path);
            return (SamePath(path, ExecutablePath) ? StartupState.On : StartupState.OtherCopy, path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return (StartupState.Off, null);
        }
    }

    public static bool Set(bool enabled, out string error)
    {
        error = "";
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (ExecutablePath is not { Length: > 0 } exe) { error = "MyNotch's location is unknown."; return false; }
                run.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else run.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = "Windows refused the startup setting.";
            return false;
        }
    }

    /// <summary>Menu/settings label for the current state.</summary>
    public static string Describe(StartupState state) => state switch
    {
        StartupState.OtherCopy => "Start with Windows (set for another copy)",
        StartupState.DisabledInTaskManager => "Start with Windows (disabled in Task Manager)",
        _ => "Start with Windows",
    };

    internal static string ExecutableFromCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : command.Trim('"');
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
