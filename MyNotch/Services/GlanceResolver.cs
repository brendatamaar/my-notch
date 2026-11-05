using MyNotch.Models;

namespace MyNotch.Services;

public enum GlanceKind { Clock, Music, Focus, Agent, Clipboard, Notice, Prayer, Meeting }

/// <summary>What the closed notch shows. <see cref="Sub"/> is the right-hand secondary text.</summary>
public sealed record GlanceState(GlanceKind Kind, string Text, string Sub = "", AgentStatus Agent = AgentStatus.Unknown);

/// <summary>Everything the resolver needs; plain data so the decision is a pure function.</summary>
public sealed record GlanceInputs
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    public AgentPayload? Agent { get; init; }
    public string? NoticeText { get; init; }
    public DateTimeOffset NoticeUntil { get; init; }
    public string? ClipboardLabel { get; init; }
    public DateTimeOffset ClipboardUntil { get; init; }
    public PomodoroPhase Phase { get; init; } = PomodoroPhase.Idle;
    public TimeSpan PhaseRemaining { get; init; }
    public bool MusicPlaying { get; init; }
    public string MusicText { get; init; } = string.Empty;
    public string? NextPrayerName { get; init; }
    public DateTime? NextPrayerAt { get; init; }
    /// <summary>Other live agent sessions besides <see cref="Agent"/>; shown as "+N".</summary>
    public int OtherAgents { get; init; }
    public Meeting? NextMeeting { get; init; }
    public TimeSpan MeetingLead { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>The chosen state, and when something time-based (expiry, minute tick) should re-run the decision.</summary>
public readonly record struct GlanceResult(GlanceState State, DateTimeOffset NextChange);

/// <summary>
/// Priority: agent awaiting approval, notice, meeting (lead time until 5 min after start), clipboard hit,
/// agent error, agent running, agent done (briefly), focus timer, music, next prayer (last 15 minutes), clock.
/// </summary>
public static class GlanceResolver
{
    public static readonly TimeSpan DoneVisible = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ErrorVisible = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RunningVisible = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PrayerLead = TimeSpan.FromMinutes(15);

    public static GlanceResult Resolve(GlanceInputs i)
    {
        var now = i.Now;
        var nextMinute = now.AddSeconds(60 - now.Second).AddMilliseconds(-now.Millisecond);
        var nextChange = nextMinute;

        void Watch(DateTimeOffset at)
        {
            if (at > now && at < nextChange) nextChange = at;
        }

        var agent = i.Agent;
        DateTimeOffset agentUntil = DateTimeOffset.MinValue;
        if (agent is not null)
        {
            agentUntil = agent.Status switch
            {
                AgentStatus.Done => agent.ReceivedAt + DoneVisible,
                AgentStatus.Error => agent.ReceivedAt + ErrorVisible,
                AgentStatus.Running => agent.ReceivedAt + RunningVisible,
                AgentStatus.AwaitingApproval => DateTimeOffset.MaxValue,
                _ => DateTimeOffset.MinValue,
            };
            if (agentUntil != DateTimeOffset.MaxValue) Watch(agentUntil);
        }
        bool agentLive = agent is not null && agentUntil > now;
        bool noticeLive = !string.IsNullOrEmpty(i.NoticeText) && i.NoticeUntil > now;
        bool clipLive = !string.IsNullOrEmpty(i.ClipboardLabel) && i.ClipboardUntil > now;
        if (noticeLive) Watch(i.NoticeUntil);
        if (clipLive) Watch(i.ClipboardUntil);

        bool meetingLive = false;
        if (i.NextMeeting is { } meeting && meeting.End > now)
        {
            var from = meeting.Start - i.MeetingLead;
            var until = meeting.Start + TimeSpan.FromMinutes(5) < meeting.End ? meeting.Start + TimeSpan.FromMinutes(5) : meeting.End;
            meetingLive = now >= from && now < until;
            Watch(from); Watch(meeting.Start); Watch(until);
        }

        GlanceState state;
        if (agentLive && agent!.Status == AgentStatus.AwaitingApproval)
            state = AgentGlance(agent, i.OtherAgents);
        else if (noticeLive)
            state = new GlanceState(GlanceKind.Notice, i.NoticeText!);
        else if (meetingLive)
        {
            var left = i.NextMeeting!.Start - now;
            state = new GlanceState(GlanceKind.Meeting, i.NextMeeting.Title, left <= TimeSpan.Zero ? "now" : $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m");
        }
        else if (clipLive)
            state = new GlanceState(GlanceKind.Clipboard, i.ClipboardLabel!, "Dev");
        else if (agentLive && agent!.Status == AgentStatus.Error)
            state = AgentGlance(agent, i.OtherAgents);
        else if (agentLive && agent!.Status is AgentStatus.Running or AgentStatus.Done)
            state = AgentGlance(agent, i.OtherAgents);
        else if (i.Phase != PomodoroPhase.Idle)
            state = new GlanceState(GlanceKind.Focus,
                i.Phase == PomodoroPhase.Focus ? "Focus" : "Break",
                FormatClock(i.PhaseRemaining));
        else if (i.MusicPlaying)
            state = new GlanceState(GlanceKind.Music, i.MusicText); // empty text = no synced lyric right now
        else if (i.NextPrayerAt is { } at && i.NextPrayerName is { } prayer
                 && at - now.LocalDateTime is var left && left > TimeSpan.Zero && left <= PrayerLead)
            state = new GlanceState(GlanceKind.Prayer, prayer, $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m");
        else
            state = new GlanceState(GlanceKind.Clock, now.LocalDateTime.ToString("HH:mm"));

        return new GlanceResult(state, nextChange);
    }

    public static string FormatClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private static GlanceState AgentGlance(AgentPayload agent, int others) =>
        new(GlanceKind.Agent, others > 0 ? $"{agent.Agent} +{others}" : agent.Agent, StatusLabel(agent.Status), agent.Status);

    public static string StatusLabel(AgentStatus status) => status switch
    {
        AgentStatus.Running => "Running",
        AgentStatus.AwaitingApproval => "Awaiting approval",
        AgentStatus.Done => "Done",
        AgentStatus.Error => "Error",
        _ => "Unknown",
    };
}
