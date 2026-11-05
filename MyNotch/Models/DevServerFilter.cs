using System.Net;
using System.Text;

namespace MyNotch.Models;

public sealed record DevProcessEvidence(string ProcessName, string Executable, string CommandLine, string ProjectRoot, bool InteractiveSession, string ProjectKind);
public static class DevServerFilter
{
    public static bool LocalEndpoint(string address) => IPAddress.TryParse(address, out var ip) &&
        (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));
    private static string Normal(string value) => value.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    public static bool Under(string file, string root) => Normal(file).StartsWith(Normal(root) + "/", StringComparison.Ordinal);
    public static bool EligibleProjectRoot(string path)
    {
        var p = "/" + Normal(path) + "/";
        return !new[] { "/node_modules/", "/site-packages/", "/.venv/", "/vendor/", "/appdata/", "/program files/", "/program files (x86)/", "/windows/", "/.vscode/extensions/", "/.local/", "/.claude/plugins/" }.Any(p.Contains);
    }
    public static bool DebuggerPort(string commandLine, int port)
    {
        var tokens = Tokens(commandLine);
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.StartsWith("--inspect", StringComparison.Ordinal))
            {
                string endpoint = token.Contains('=') ? token[(token.IndexOf('=') + 1)..] : "9229";
                if (Port(endpoint) == port) return true;
            }
            if (commandLine.Contains("debugpy", StringComparison.OrdinalIgnoreCase) && token == "--listen" && i + 1 < tokens.Count && Port(tokens[i + 1]) == port) return true;
            if (token.Contains("jdwp", StringComparison.OrdinalIgnoreCase))
                foreach (var parameter in token.Split(',')) if (parameter.StartsWith("address=", StringComparison.OrdinalIgnoreCase) && Port(parameter[8..]) == port) return true;
        }
        return false;
        static int Port(string endpoint) => int.TryParse(endpoint[(endpoint.LastIndexOf(':') + 1)..], out var value) ? value : -1;
    }
    public static string? Classify(DevProcessEvidence evidence)
    {
        if (!evidence.InteractiveSession || string.IsNullOrWhiteSpace(evidence.ProjectRoot) || !EligibleProjectRoot(evidence.ProjectRoot)) return null;
        string command = Normal(evidence.CommandLine);
        string name = Normal(evidence.ProcessName).Replace(".exe", "");
        if (new[] { "language-server", "languageserver", "tsserver", "extensionhost", "--type=", "app-server", "modelcontextprotocol", "mcp-server", "mcp_server", "/mcp/", "/mcp-", "-mcp/", "/mcp.", "--stdio", "ipykernel", "jupyter", "idea_rt.jar", "gradledaemon" }.Any(command.Contains)) return null;
        var tokens = Tokens(evidence.CommandLine);
        var scripts = tokens.Skip(1).Where(t => new[] { ".js", ".mjs", ".cjs", ".ts", ".py", ".dll", ".jar", ".php" }.Any(ext => t.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool ownScript = scripts.Any(t => !Path.IsPathFullyQualified(t) || Under(t, evidence.ProjectRoot));
        bool externalOnly = scripts.Length > 0 && !ownScript;
        bool knownGlobalDevTool = command.Contains("/vite/bin/vite.") || command.Contains("/@angular/cli/bin/ng.js") && tokens.Contains("serve") || command.Contains("/webpack-cli/bin/cli.js") && tokens.Contains("serve");
        if (externalOnly && !knownGlobalDevTool) return null;
        if (name is "node" or "bun" or "deno")
        {
            if (command.Contains("vite")) return "Vite";
            if (command.Contains("next/") || command.Contains("next dev") || command.Contains("next-server")) return "Next.js";
            if (command.Contains("nuxt")) return "Nuxt";
            if (command.Contains("astro")) return "Astro";
            if (command.Contains("webpack") || command.Contains("react-scripts") || command.Contains("angular")) return "Web dev server";
            return ownScript || tokens.Contains("--watch") || command.Contains("createserver") || name == "deno" && tokens.Contains("serve") ? "JavaScript server" : null;
        }
        if (name.StartsWith("python", StringComparison.Ordinal))
            return command.Contains("uvicorn") || command.Contains("gunicorn") || command.Contains("hypercorn") || command.Contains("http.server") || command.Contains("runserver") || command.Contains("flask") || ownScript ? "Python server" : null;
        if (name == "dotnet") return evidence.ProjectKind == "dotnet-web" && (ownScript || tokens.Contains("run") || tokens.Contains("watch")) ? "ASP.NET Core" : null;
        if (evidence.ProjectKind == "dotnet-web" && Under(evidence.Executable, evidence.ProjectRoot) && (Normal(evidence.Executable).Contains("/bin/") || Normal(evidence.Executable).Contains("/obj/"))) return "ASP.NET Core";
        if (name.StartsWith("php", StringComparison.Ordinal) && (tokens.Any(t => t.Equals("-S", StringComparison.Ordinal)) || command.Contains("artisan"))) return "PHP server";
        if (name.StartsWith("ruby", StringComparison.Ordinal) && (command.Contains("rails") || command.Contains("puma") || command.Contains("rackup"))) return "Ruby server";
        if (name is "java" or "javaw") return ownScript || command.Contains("spring") || command.Contains("quarkus") ? "Java server" : null;
        if (name == "iisexpress" && (command.Contains("/config:") || command.Contains("/path:"))) return "IIS Express";
        if (evidence.ProjectKind == "go" && (Under(evidence.Executable, evidence.ProjectRoot) || Normal(evidence.Executable).Contains("/go-build"))) return "Go server";
        if (evidence.ProjectKind == "rust" && Under(evidence.Executable, evidence.ProjectRoot) && Normal(evidence.Executable).Contains("/target/")) return "Rust server";
        return null;
    }
    /// <summary>Windows command-line quoting, including backslashes immediately before quotes.</summary>
    public static IReadOnlyList<string> Tokens(string command)
    {
        var result = new List<string>(); int i = 0;
        while (i < command.Length)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i])) i++;
            if (i == command.Length) break;
            bool quoted = false; var token = new StringBuilder();
            while (i < command.Length && (quoted || !char.IsWhiteSpace(command[i])))
            {
                int slashes = 0;
                while (i < command.Length && command[i] == '\\') { slashes++; i++; }
                if (i < command.Length && command[i] == '"')
                {
                    token.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) token.Append('"'); else quoted = !quoted;
                    i++;
                }
                else { token.Append('\\', slashes); if (i < command.Length && (quoted || !char.IsWhiteSpace(command[i]))) token.Append(command[i++]); }
            }
            result.Add(token.ToString());
        }
        return result;
    }
}
