using System.Text.Json;

namespace MyNotch.Services;

public sealed record GitStatus(string Branch, string Upstream, int Ahead, int Behind, int Changes);
public sealed record CiRun(string Workflow, string Title, string Status, string Conclusion, string Url)
{
    public bool InProgress => Status is not "completed" and not "";
    public bool Passed => Status == "completed" && Conclusion == "success";
    public bool Failed => Status == "completed" && Conclusion is "failure" or "timed_out" or "startup_failure" or "action_required";
}
public sealed record RepoSnapshot(string Root, GitStatus? Git, CiRun? Ci);

/// <summary>git + GitHub CLI reads for a handful of known repositories. On demand only; CI results are cached for two minutes.</summary>
public sealed class GitStatusService
{
    private static readonly TimeSpan CiCacheTime = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, (DateTimeOffset At, CiRun? Run)> _ci = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private static string? Git => CliRunner.Find("git.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"));
    private static string? Gh => CliRunner.Find("gh.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"));

    /// <summary>Walks up from <paramref name="path"/> to the folder holding .git (directory or worktree file).</summary>
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            if (!dir.Exists) dir = dir.Parent;
            for (int depth = 0; dir is not null && depth < 16; depth++, dir = dir.Parent)
            {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return dir.FullName.TrimEnd('\\', '/');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    public async Task<RepoSnapshot> ReadAsync(string root, bool forceCi, CancellationToken ct = default)
    {
        if (Git is not { } git) return new(root, null, null);
        var result = await CliRunner.RunAsync(git, ["-C", root, "--no-optional-locks", "status", "--porcelain=v2", "--branch"], TimeSpan.FromSeconds(8), root, ct).ConfigureAwait(false);
        var status = result is { Ok: true } ok ? ParseStatus(ok.Output) : null;
        CiRun? ci = status is { Branch.Length: > 0 } && IsGitHub(root) ? await ReadCiAsync(root, status.Branch, forceCi, ct).ConfigureAwait(false) : null;
        return new(root, status, ci);
    }

    private async Task<CiRun?> ReadCiAsync(string root, string branch, bool force, CancellationToken ct)
    {
        string key = root + "\u001f" + branch;
        lock (_gate)
        {
            if (!force && _ci.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CiCacheTime) return cached.Run;
        }
        if (Gh is not { } gh) return null;
        var result = await CliRunner.RunAsync(gh, ["run", "list", "--branch", branch, "--limit", "1", "--json", "status,conclusion,workflowName,displayTitle,url"], TimeSpan.FromSeconds(15), root, ct).ConfigureAwait(false);
        CiRun? run = result is { Ok: true } ok ? ParseRuns(ok.Output) : null;
        lock (_gate)
        {
            _ci[key] = (DateTimeOffset.UtcNow, run);
            if (_ci.Count > 64) _ci.Clear();
        }
        return run;
    }

    private static bool IsGitHub(string root)
    {
        try
        {
            string config = Path.Combine(root, ".git", "config");
            return File.Exists(config) && new FileInfo(config).Length < 256 * 1024 && File.ReadAllText(config).Contains("github.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Parses `git status --porcelain=v2 --branch`.</summary>
    public static GitStatus ParseStatus(string output)
    {
        string branch = "", upstream = ""; int ahead = 0, behind = 0, changes = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line[14..];
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = line[18..];
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                foreach (var part in line[12..].Split(' '))
                {
                    if (part.StartsWith('+') && int.TryParse(part[1..], out int a)) ahead = a;
                    else if (part.StartsWith('-') && int.TryParse(part[1..], out int b)) behind = b;
                }
            }
            else if (line.Length > 1 && line[0] is '1' or '2' or 'u' or '?' && line[1] == ' ') changes++;
        }
        if (branch == "(detached)") branch = "detached HEAD";
        return new(branch, upstream, ahead, behind, changes);
    }

    /// <summary>Parses `gh run list --json status,conclusion,workflowName,displayTitle,url` (first run).</summary>
    public static CiRun? ParseRuns(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            var run = doc.RootElement[0];
            string Get(string name) => run.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string url = Get("url");
            return new(Get("workflowName"), Get("displayTitle"), Get("status"), Get("conclusion"), url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : "");
        }
        catch (JsonException) { return null; }
    }
}
