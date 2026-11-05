namespace MyNotch.Models;

/// <summary>Latest known state of one agent session.</summary>
public sealed record AgentSession(string Key, AgentPayload Latest)
{
    public string Agent => Latest.Agent;
    public AgentStatus Status => Latest.Status;
    public string Message => Latest.Message;
    public string Project => string.IsNullOrWhiteSpace(Latest.Cwd) ? "" : Path.GetFileName(Latest.Cwd.TrimEnd('\\', '/'));
    public string Title => Project.Length > 0 ? $"{Agent} · {Project}" : Agent;
    public string TimeText => Latest.TimeText;
}

/// <summary>Pure session bookkeeping: newest first, bounded, finished sessions age out.</summary>
public static class AgentSessions
{
    public const int Max = 6;
    public static readonly TimeSpan FinishedLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RunningLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan WaitingLifetime = TimeSpan.FromHours(8);

    public static string KeyOf(AgentPayload payload) =>
        !string.IsNullOrWhiteSpace(payload.Session) ? payload.Agent + "\u001f" + payload.Session.Trim()
        : payload.Pid is > 0 ? payload.Agent + "\u001f#" + payload.Pid
        : payload.Agent;

    public static IReadOnlyList<AgentSession> Apply(IReadOnlyList<AgentSession> sessions, AgentPayload payload, DateTimeOffset now)
    {
        string key = KeyOf(payload);
        var previous = sessions.FirstOrDefault(s => s.Key == key);
        // Later hooks may not repeat the sender's window or folder; keep what an earlier event told us.
        if (previous is not null)
            payload = payload with
            {
                Pid = payload.Pid ?? previous.Latest.Pid,
                Hwnd = payload.Hwnd ?? previous.Latest.Hwnd,
                Cwd = string.IsNullOrWhiteSpace(payload.Cwd) ? previous.Latest.Cwd : payload.Cwd,
            };
        var next = new List<AgentSession>(sessions.Count + 1) { new(key, payload) };
        next.AddRange(sessions.Where(s => s.Key != key));
        return Prune(next, now);
    }

    public static IReadOnlyList<AgentSession> Remove(IReadOnlyList<AgentSession> sessions, string key) =>
        sessions.Any(s => s.Key == key) ? sessions.Where(s => s.Key != key).ToArray() : sessions;

    public static IReadOnlyList<AgentSession> Prune(IReadOnlyList<AgentSession> sessions, DateTimeOffset now) =>
        sessions.Where(s => now - s.Latest.ReceivedAt < s.Status switch
        {
            AgentStatus.Running => RunningLifetime,
            AgentStatus.AwaitingApproval => WaitingLifetime,
            _ => FinishedLifetime,
        }).Take(Max).ToArray();

    /// <summary>The session the closed notch should show: one waiting on the user first, else the newest.</summary>
    public static AgentSession? Attention(IReadOnlyList<AgentSession> sessions) =>
        sessions.FirstOrDefault(s => s.Status == AgentStatus.AwaitingApproval) ?? sessions.FirstOrDefault();
}
