namespace MyNotch.Models;

/// <summary>One listening TCP endpoint mapped to its owning process (PLAN Phase 3, PRD §3.3).</summary>
public sealed record ListeningPortInfo
{
    public int Port { get; init; }
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string Protocol { get; init; } = "TCPv4";

    public string Address { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string Framework { get; init; } = "";
    public ulong Created { get; init; }
    public string CommandLine { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Subtitle => $"{ProcessName} · PID {Pid}";
    public string OpenUrl => $"http://localhost:{Port}/";
    public string ProjectName => Path.GetFileName(ProjectPath.TrimEnd('\\', '/'));
    public string ProjectLabel => $"{ProjectName} | {Framework}";
    public string StopToken => $"{Pid}:{Port}:{Created}";

    public string DisplayText => $":{Port} — {ProcessName} ({Pid})";
}
