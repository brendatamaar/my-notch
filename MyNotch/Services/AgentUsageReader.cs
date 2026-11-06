using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyNotch.Models;

namespace MyNotch.Services;

public interface IAgentUsageReader
{
    Task<UsageOutcome> ReadAsync(UsageProvider provider, CancellationToken cancellationToken);
}

/// <summary>Quota reads and explicit account-provided reset requests. Never starts a model turn.</summary>
public interface IUsageResetter
{
    Task<UsageResetOutcome> ResetCodexAsync(UsageResetCredit credit, CancellationToken cancellationToken);
}

public sealed class AgentUsageReader : IAgentUsageReader, IUsageResetter, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private const int MaxOutput = 1024 * 1024;
    public void Dispose() => _http.Dispose();

    public async Task<UsageOutcome> ReadAsync(UsageProvider provider, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            return provider switch
            {
                UsageProvider.Claude => await ClaudeAsync(timeout.Token).ConfigureAwait(false),
                UsageProvider.Codex => await CodexAsync(timeout.Token).ConfigureAwait(false),
                UsageProvider.Muse => await MuseAsync(timeout.Token).ConfigureAwait(false),
                UsageProvider.Antigravity => await AntigravityAsync(timeout.Token).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, UsageProblem.Offline); }
        catch (JsonException) { return new(null, UsageProblem.InvalidData); }
        catch (UnauthorizedAccessException) { return new(null, UsageProblem.SignInRequired); }
        catch (FileNotFoundException) { return new(null, UsageProblem.NotInstalled); }
        catch (Win32Exception) { return new(null, UsageProblem.NotInstalled); }
        catch (HttpRequestException) { return new(null, UsageProblem.Offline); }
        catch (IOException) { return new(null, UsageProblem.InvalidData); }
        // Oversize or missing CLI replies. Not an IOException, so it needs its own catch.
        catch (InvalidDataException) { return new(null, UsageProblem.InvalidData); }
    }

    private async Task<UsageOutcome> ClaudeAsync(CancellationToken ct)
    {
        string directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Profile, ".claude");
        string credentials = Path.Combine(directory, ".credentials.json");
        if (!File.Exists(credentials)) return new(null, UsageProblem.SignInRequired);
        using var auth = JsonDocument.Parse(await ReadSmallFileAsync(credentials, ct).ConfigureAwait(false));
        var oauth = AgentUsageParser.Get(auth.RootElement, "claudeAiOauth");
        string? token = AgentUsageParser.Text(AgentUsageParser.Get(oauth, "accessToken"));
        if (string.IsNullOrWhiteSpace(token)) return new(null, UsageProblem.SignInRequired);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, UsageProblem.SignInRequired);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(null, UsageProblem.RateLimited, response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5)));
        if (response.StatusCode == HttpStatusCode.Forbidden) return new(null, UsageProblem.Unsupported);
        if (!response.IsSuccessStatusCode) return new(null, UsageProblem.Offline);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body);
        var outcome = AgentUsageParser.Parse(UsageProvider.Claude, await ReadBoundedAsync(reader, ct).ConfigureAwait(false), DateTimeOffset.UtcNow);
        string? plan = AgentUsageParser.Text(AgentUsageParser.Get(oauth, "subscriptionType"));
        return outcome.Snapshot is { } snapshot ? outcome with { Snapshot = snapshot with { Plan = plan is { Length: <= 60 } ? plan : null } } : outcome;
    }

    private static async Task<UsageOutcome> CodexAsync(CancellationToken ct)
    {
        var response = await CodexRpcAsync("account/rateLimits/read", null, ct).ConfigureAwait(false);
        if (AgentUsageParser.Get(response, "error").ValueKind == JsonValueKind.Object)
        {
            var message = AgentUsageParser.Text(AgentUsageParser.Get(AgentUsageParser.Get(response, "error"), "message")) ?? "";
            return new(null, message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("401") || message.Contains("signed", StringComparison.OrdinalIgnoreCase) ? UsageProblem.SignInRequired : UsageProblem.Offline);
        }
        return AgentUsageParser.Parse(UsageProvider.Codex, response.GetRawText(), DateTimeOffset.UtcNow);
    }

    public async Task<UsageResetOutcome> ResetCodexAsync(UsageResetCredit credit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var journal = new UsageResetJournal();
            string requestKey = journal.GetOrCreate(credit.Id);
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject(); json.WriteString("idempotencyKey", requestKey); json.WriteString("creditId", credit.Id); json.WriteEndObject();
            }
            var response = await CodexRpcAsync("account/rateLimitResetCredit/consume", Encoding.UTF8.GetString(buffer.ToArray()), timeout.Token).ConfigureAwait(false);
            var outcome = AgentUsageParser.Text(AgentUsageParser.Get(AgentUsageParser.Get(response, "result"), "outcome")) switch
            {
                "reset" => UsageResetOutcome.Reset,
                "alreadyRedeemed" => UsageResetOutcome.AlreadyRedeemed,
                "nothingToReset" => UsageResetOutcome.NothingToReset,
                "noCredit" => UsageResetOutcome.NoCredit,
                _ => UsageResetOutcome.Unknown,
            };
            journal.Record(credit.Id, outcome);
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or JsonException or Win32Exception)
        {
            return UsageResetOutcome.Unknown;
        }
    }

    private static async Task<JsonElement> CodexRpcAsync(string method, string? parameters, CancellationToken ct)
    {
        string? executable = FindExecutable("codex.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        if (executable is null) throw new FileNotFoundException();
        using var process = Start(executable, ["app-server", "--listen", "stdio://"]);
        var stderr = ReadBoundedAsync(process.StandardError, ct);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"my_notch_usage\",\"version\":\"0.1.0\"}}}").ConfigureAwait(false);
            var initialized = await ReadRpcAsync(process, 1, ct).ConfigureAwait(false);
            if (AgentUsageParser.Get(initialized, "error").ValueKind == JsonValueKind.Object) return initialized;
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}").ConfigureAwait(false);
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject(); json.WriteNumber("id", 2); json.WriteString("method", method); json.WritePropertyName("params");
                if (parameters is null) json.WriteNullValue(); else json.WriteRawValue(parameters);
                json.WriteEndObject();
            }
            await process.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
            return await ReadRpcAsync(process, 2, ct).ConfigureAwait(false);
        }
        finally { Kill(process); await IgnoreDrainAsync(stderr).ConfigureAwait(false); }
    }

    private static async Task<JsonElement> ReadRpcAsync(Process process, int id, CancellationToken ct)
    {
        for (int count = 0; count < 128; count++)
        {
            string? line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null || line.Length > MaxOutput) throw new InvalidDataException("Quota RPC response unavailable");
            using var doc = JsonDocument.Parse(line);
            var responseId = AgentUsageParser.Get(doc.RootElement, "id");
            if (responseId.ValueKind == JsonValueKind.Number && responseId.TryGetInt32(out int resultId) && resultId == id) return doc.RootElement.Clone();
        }
        throw new InvalidDataException("Quota RPC did not reply");
    }

    private static async Task<UsageOutcome> AntigravityAsync(CancellationToken ct)
    {
        string? executable = FindExecutable("agy.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe"));
        if (executable is null) return new(null, UsageProblem.NotInstalled);
        var versionReply = await RunAsync(executable, ["--version"], ct).ConfigureAwait(false);
        var match = Regex.Match(versionReply.Output, @"\b(\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant);
        if (versionReply.ExitCode != 0 || !match.Success || !Version.TryParse(match.Groups[1].Value, out var version) || version < new Version(1, 1, 11)) return new(null, UsageProblem.Unsupported);
        DeferAgyUpdateCheck(AgyUpdateStamp, DateTime.UtcNow);
        var reply = await RunAsync(executable, ["-p", "/usage", "--output-format", "json"], ct).ConfigureAwait(false);
        if (reply.ExitCode != 0) return new(null, ClassifyCliFailure(reply.Output + " " + reply.Error));
        foreach (string line in reply.Output.Split('\n').Reverse())
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            var parsed = AgentUsageParser.Parse(UsageProvider.Antigravity, line, DateTimeOffset.UtcNow);
            if (parsed.Problem != UsageProblem.InvalidData) return parsed;
        }
        return new(null, UsageProblem.InvalidData);
    }

    private static async Task<UsageOutcome> MuseAsync(CancellationToken ct)
    {
        string? executable = FindExecutable("muse.exe");
        if (executable is null)
        {
            string? launcher = FindExecutable("muse.cmd", Path.Combine(Profile, ".local", "bin", "muse.cmd"));
            if (launcher is null) return new(null, UsageProblem.NotInstalled);
            string directory = Path.GetDirectoryName(launcher)!;
            string versionFile = Path.Combine(directory, ".muse-version");
            if (!File.Exists(versionFile)) return new(null, UsageProblem.NotInstalled);
            string version = (await ReadSmallFileAsync(versionFile, ct).ConfigureAwait(false)).Trim();
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+-R\d+(\.\d+)?$", RegexOptions.CultureInvariant)) return new(null, UsageProblem.Unsupported);
            executable = Path.Combine(directory, $"muse-bin-{version}.exe");
            if (!File.Exists(executable)) return new(null, UsageProblem.NotInstalled);
        }
        var reply = await RunAsync(executable, ["usage", "--json"], ct).ConfigureAwait(false);
        return reply.ExitCode != 0 ? new(null, ClassifyCliFailure(reply.Output + " " + reply.Error)) : AgentUsageParser.Parse(UsageProvider.Muse, reply.Output, DateTimeOffset.UtcNow);
    }

    private static UsageProblem ClassifyCliFailure(string message)
    {
        if (new[] { "429", "rate limit", "rate-limit", "too many requests" }.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase))) return UsageProblem.RateLimited;
        if (new[] { "not logged", "log in", "login", "unauthorized", "401", "credential", "sign in", "token expired" }.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase))) return UsageProblem.SignInRequired;
        return UsageProblem.Offline;
    }

    private static string? FindExecutable(string name, params string[] known)
    {
        foreach (string file in known) if (File.Exists(file)) return file;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), name);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }
    private static Process Start(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetTempPath() };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("CLI could not start");
    }
    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        using var process = Start(executable, arguments);
        var stdout = ReadBoundedAsync(process.StandardOutput, ct);
        var stderr = ReadBoundedAsync(process.StandardError, ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), error);
        }
        finally { Kill(process); await IgnoreDrainAsync(stdout).ConfigureAwait(false); await IgnoreDrainAsync(stderr).ConfigureAwait(false); }
    }

    /// <summary>
    /// agy runs a background update check whenever its last one (the mtime of last_check.timestamp) is more than
    /// 15 minutes old. That updater is a detached process whose `agy --version` child gets a brand-new console,
    /// and Windows 11 hands every new console to Windows Terminal: a terminal window flashes on screen. Our hidden
    /// console can't cover it. Backdating the stamp to 14 minutes ago makes this one read take agy's "checked
    /// recently" fast path, while any agy run of the user's own a minute later still checks for updates normally.
    /// Only the file's timestamp is touched, and only when agy has created it.
    /// </summary>
    internal static void DeferAgyUpdateCheck(string stampPath, DateTime utcNow)
    {
        try
        {
            var stamp = new FileInfo(stampPath);
            var recent = utcNow - TimeSpan.FromMinutes(14);
            if (stamp.Exists && stamp.LastWriteTimeUtc < recent) File.SetLastWriteTimeUtc(stampPath, recent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static string AgyUpdateStamp => Path.Combine(Profile, ".gemini", "antigravity-cli", "last_check.timestamp");
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } }
    private static async Task IgnoreDrainAsync(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { } }
    private static async Task<string> ReadSmallFileAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxOutput) throw new InvalidDataException("Oversize local settings");
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > MaxOutput) throw new InvalidDataException("Oversize quota response");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
