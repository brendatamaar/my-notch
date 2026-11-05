using MyNotch.Interop;
using MyNotch.Models;

namespace MyNotch.Services;

internal sealed record DevServerIdentity(string Name, string ProjectRoot, string Framework, ulong Created, string CommandLine, string WorkingDirectory = "");
internal static class DevServerResolver
{
    internal static DevServerIdentity? Resolve(int pid)
    {
        var process = DevProcessInspector.Read(pid);
        if (process is null || string.IsNullOrWhiteSpace(process.CommandLine)) return null;
        var candidates = new List<string>();
        foreach (var token in DevServerFilter.Tokens(process.CommandLine).Skip(1))
        {
            string value = token;
            if (value.StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) value = value[10..];
            if (value.StartsWith("/config:", StringComparison.OrdinalIgnoreCase)) value = value[8..];
            if (Path.IsPathFullyQualified(value)) candidates.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(process.WorkingDirectory)) candidates.Add(process.WorkingDirectory);
        string runtime = process.Name.ToLowerInvariant();
        if (!(runtime is "node" or "bun" or "deno" or "dotnet" or "java" or "javaw") && !runtime.StartsWith("python") && !runtime.StartsWith("php") && !runtime.StartsWith("ruby")) candidates.Add(process.Executable);
        foreach (var candidate in candidates)
        {
            var project = FindProject(candidate);
            if (project is null) continue;
            var framework = DevServerFilter.Classify(new(process.Name, process.Executable, process.CommandLine, project.Value.Root, true, project.Value.Kind));
            if (framework is not null) return new(process.Name, project.Value.Root, framework, process.Created, process.CommandLine, process.WorkingDirectory);
        }
        return null;
    }
    private static (string Root, string Kind)? FindProject(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            var directory = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
            for (int depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
            {
                if (directory.Parent is null || directory.FullName.TrimEnd('\\', '/').Equals(home, StringComparison.OrdinalIgnoreCase)) break;
                if (!DevServerFilter.EligibleProjectRoot(directory.FullName)) continue;
                foreach (var csproj in directory.EnumerateFiles("*.csproj").Concat(directory.EnumerateFiles("*.fsproj")))
                    if (csproj.Length < 256 * 1024 && File.ReadAllText(csproj.FullName).Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) return (directory.FullName.TrimEnd('\\', '/'), "dotnet-web");
                foreach (var (marker, kind) in new[] { ("package.json", "javascript"), ("pyproject.toml", "python"), ("requirements.txt", "python"), ("manage.py", "python"), ("go.mod", "go"), ("Cargo.toml", "rust"), ("pom.xml", "java"), ("build.gradle", "java"), ("build.gradle.kts", "java"), ("composer.json", "php"), ("Gemfile", "ruby") })
                    if (File.Exists(Path.Combine(directory.FullName, marker))) return (directory.FullName.TrimEnd('\\', '/'), kind);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }
}
