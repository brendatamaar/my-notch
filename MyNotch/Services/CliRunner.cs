using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MyNotch.Services;

public readonly record struct CliResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs a hidden CLI with bounded output and a hard timeout; the process tree is killed on timeout.</summary>
public static class CliRunner
{
    private const int MaxOutput = 512 * 1024;

    /// <summary>Returns null when the executable can't start or the call times out / is cancelled.</summary>
    public static async Task<CliResult?> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, string? workingDirectory = null, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory is { Length: > 0 } && Directory.Exists(workingDirectory) ? workingDirectory : Path.GetTempPath(),
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // Git and gh must never wait on a credential or pager prompt nobody can see.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["NO_COLOR"] = "1";

        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException) { return null; }
        if (process is null) return null;
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            var stdout = ReadBoundedAsync(process.StandardOutput, limit.Token);
            var stderr = ReadBoundedAsync(process.StandardError, limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return new CliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException) { return null; }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                await Ignore(stdout).ConfigureAwait(false);
                await Ignore(stderr).ConfigureAwait(false);
            }
        }
    }

    /// <summary>First match on known paths, then PATH.</summary>
    public static string? Find(string name, params string[] known)
    {
        foreach (var file in known) if (File.Exists(file)) return file;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), name);
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > MaxOutput) throw new InvalidDataException("CLI output too large");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static async Task Ignore(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException or ObjectDisposedException) { }
    }
}
