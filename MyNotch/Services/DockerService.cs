using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyNotch.Services;

public sealed record DockerContainer(string Id, string Name, string Image, string State, IReadOnlyList<int> HostPorts, string PortsText);
public sealed record DockerSnapshot(bool Installed, bool Running, IReadOnlyList<DockerContainer> Containers, string Status);

/// <summary>Lists running containers through the Docker CLI on demand (only while the Dev tab is open). No polling.</summary>
public static partial class DockerService
{
    private static string? Executable => CliRunner.Find("docker.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"));

    public static async Task<DockerSnapshot> ListAsync(CancellationToken ct = default)
    {
        if (Executable is not { } docker) return new(false, false, [], "");
        var result = await CliRunner.RunAsync(docker, ["ps", "--no-trunc", "--format", "{{json .}}"], TimeSpan.FromSeconds(8), ct: ct).ConfigureAwait(false);
        if (result is null) return new(true, false, [], "Docker didn't answer");
        if (!result.Value.Ok)
            return new(true, false, [], result.Value.Error.Contains("pipe", StringComparison.OrdinalIgnoreCase) || result.Value.Error.Contains("daemon", StringComparison.OrdinalIgnoreCase)
                ? "Docker isn't running" : "Docker CLI error");
        var containers = Parse(result.Value.Output);
        return new(true, true, containers, containers.Count == 0 ? "No running containers" : $"{containers.Count} running");
    }

    public static async Task<(bool Ok, string Error)> StopAsync(string id, CancellationToken ct = default)
    {
        if (Executable is not { } docker) return (false, "Docker CLI not found");
        if (!ContainerId().IsMatch(id)) return (false, "Invalid container id");
        var result = await CliRunner.RunAsync(docker, ["stop", id], TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
        return result is { Ok: true } ? (true, "") : (false, result?.Error.Trim() is { Length: > 0 } e ? e : "docker stop failed");
    }

    /// <summary>Parses `docker ps --format "{{json .}}"` output (one JSON object per line).</summary>
    public static IReadOnlyList<DockerContainer> Parse(string output)
    {
        var list = new List<DockerContainer>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                string ports = Get("Ports");
                var hostPorts = HostPort().Matches(ports).Select(m => int.Parse(m.Groups[1].Value)).Where(p => p is > 0 and <= 65535).Distinct().Order().ToArray();
                string id = Get("ID");
                if (id.Length == 0) continue;
                list.Add(new(id, Get("Names"), Get("Image"), Get("State"), hostPorts, hostPorts.Length == 0 ? "" : string.Join(", ", hostPorts.Select(p => ":" + p))));
            }
            catch (JsonException) { }
        }
        return list;
    }

    [GeneratedRegex(@"(?:\d{1,3}(?:\.\d{1,3}){3}|\[?::\]?):(\d+)->\d+/tcp")]
    private static partial Regex HostPort();

    [GeneratedRegex(@"^[0-9a-f]{12,64}$")]
    private static partial Regex ContainerId();
}
