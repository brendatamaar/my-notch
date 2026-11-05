using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// Phase 5 command palette backend (PRD §3.7): allowlist of saved commands loaded
/// from <c>%LOCALAPPDATA%\MyNotch\commands.json</c> or beside the exe (built-ins when missing), substring
/// filtering, and execution via <see cref="Process"/>. Unmatched free text never
/// executes. Targets support <c>%ENV%</c> expansion; every launch is validated first.
/// </summary>
public sealed class CommandPaletteService
{
    internal const string FileName = "commands.json";
    internal const int MaxMatches = 8;

    private readonly List<SavedCommand> _commands;

    public IReadOnlyList<SavedCommand> Commands => _commands;

    public CommandPaletteService() : this(LoadCommands()) { }

    internal CommandPaletteService(List<SavedCommand> commands)
    {
        _commands = commands.Where(c => c.IsValid).ToList();
        if (_commands.Count == 0)
            _commands.AddRange(DefaultCommands());
    }

    public static List<SavedCommand> DefaultCommands() => new()
    {
        new SavedCommand { Name = "Terminal", Kind = "terminal", Target = "%USERPROFILE%", Keywords = "wt console shell" },
    };

    public static List<SavedCommand> LoadCommands(string? path = null)
    {
        // Prefer the per-user settings folder (survives rebuilds/redeploys), then the copy beside the exe.
        path ??= new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", FileName),
            Path.Combine(AppContext.BaseDirectory, FileName),
        }.FirstOrDefault(File.Exists);
        if (path is null) return DefaultCommands();
        try
        {
            if (!File.Exists(path))
                return DefaultCommands();
            var parsed = ParseCommands(File.ReadAllText(path));
            return parsed.Count == 0 ? DefaultCommands() : parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultCommands();
        }
    }

    public static List<SavedCommand> ParseCommands(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize(json, CommandsJsonContext.Default.ListSavedCommand);
            return (list ?? new()).Where(c => c.IsValid).ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public List<SavedCommand> Filter(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _commands.Take(MaxMatches).ToList();
        var q = query.Trim();
        return _commands
            .Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Target.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(MaxMatches)
            .ToList();
    }

    public bool TryExecute(SavedCommand command, out string error)
    {
        error = string.Empty;
        try
        {
            switch ((command.Kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "terminal":
                {
                    var dir = Expand(command.Target);
                    var psi = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = true };
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        if (!Directory.Exists(dir))
                        {
                            error = $"Folder not found: {dir}";
                            return false;
                        }
                        // A trailing backslash would escape the closing quote ("C:\" -> C:"), so double it.
                        psi.Arguments = $"-d \"{(dir.EndsWith('\\') ? dir + "\\" : dir)}\"";
                        psi.WorkingDirectory = dir;
                    }
                    return Start(psi, out error);
                }
                case "ssh":
                {
                    var host = Expand(command.Target);
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        error = "SSH entry has no host.";
                        return false;
                    }
                    return Start(new ProcessStartInfo { FileName = "ssh", Arguments = host, UseShellExecute = true }, out error);
                }
                case "script":
                case "ps1":
                case "powershell":
                {
                    var path = Expand(command.Target);
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        error = $"Script not found: {command.Target}";
                        return false;
                    }
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                        UseShellExecute = true,
                    };
                    try { psi.WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty; } catch { }
                    return Start(psi, out error);
                }
                default:
                    error = $"Unknown command kind '{command.Kind}'.";
                    return false;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException or FileNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool Start(ProcessStartInfo psi, out string error)
    {
        error = string.Empty;
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "Process failed to start.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException or FileNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static string Expand(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
}
