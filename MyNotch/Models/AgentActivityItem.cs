namespace MyNotch.Models;

/// <summary>Read-only display projection, keeping immutable webhook payloads out of XAML setters.</summary>
public sealed class AgentActivityItem(AgentPayload payload)
{
    public string Agent => payload.Agent;
    public string Message => payload.Message;
    public AgentStatus Status => payload.Status;
    public string TimeText => payload.TimeText;
}

/// <summary>Read-only display projection of an <see cref="AgentSession"/> for the sessions list.</summary>
public sealed class AgentSessionItem(AgentSession session)
{
    public string Key => session.Key;
    public string Title => session.Title;
    public string Message => session.Message;
    public AgentStatus Status => session.Status;
    public string TimeText => session.TimeText;
}
