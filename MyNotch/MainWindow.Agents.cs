using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyNotch.Interop;
using MyNotch.Models;
using MyNotch.ViewModels;

namespace MyNotch;

/// <summary>Concurrent agent sessions and Claude Code permission prompts answered from the notch.</summary>
public sealed partial class MainWindow
{
    private sealed record PendingApproval(ApprovalRequest Request, TaskCompletionSource<ApprovalDecision> Decision);

    private IReadOnlyList<AgentSession> _sessions = Array.Empty<AgentSession>();
    private readonly List<PendingApproval> _approvals = new();
    public ObservableCollection<AgentSessionItem> SessionItems { get; } = new();

    private void InitializeAgents()
    {
        _webhook.SessionEnded += (_, key) => _dispatcher.TryEnqueue(() =>
        {
            _sessions = AgentSessions.Remove(_sessions, key);
            SyncSessions();
            RefreshGlance();
        });
        _webhook.ApprovalHandler = HandleApprovalAsync;
    }

    private void TrackSession(AgentPayload payload)
    {
        _sessions = AgentSessions.Apply(_sessions, payload, DateTimeOffset.Now);
        SyncSessions();
    }

    private void SyncSessions()
    {
        _sessions = AgentSessions.Prune(_sessions, DateTimeOffset.Now);
        SessionItems.Clear();
        foreach (var session in _sessions) SessionItems.Add(new AgentSessionItem(session));
        SessionsPanel.Visibility = _sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateUsagePanelHeight();
    }

    /// <summary>The session the closed notch shows, and how many others are live.</summary>
    private (AgentPayload? Agent, int Others) AgentForGlance()
    {
        var live = AgentSessions.Prune(_sessions, DateTimeOffset.Now);
        var attention = AgentSessions.Attention(live);
        return attention is null ? (AgentAlert.ViewModel.Latest, 0) : (attention.Latest, Math.Max(0, live.Count - 1));
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key } && _sessions.FirstOrDefault(s => s.Key == key) is { } session)
            WindowHelper.FocusSender(session.Latest.Hwnd, session.Latest.Pid);
    }

    // ---------------------------------------------------------------- approvals

    /// <summary>Runs on a listener thread. Waits for the user, or answers "no decision" after the configured wait.</summary>
    private async Task<ApprovalDecision> HandleApprovalAsync(ApprovalRequest request)
    {
        var settings = _settings.Current;
        if (!settings.NotchApprovals) return ApprovalDecision.None;
        var pending = new PendingApproval(request, new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_dispatcher.TryEnqueue(() => EnqueueApproval(pending))) return ApprovalDecision.None;
        // Cancel the wait once the user decides, so each prompt doesn't leave a timer running for the full wait.
        using var wait = new CancellationTokenSource();
        var finished = await Task.WhenAny(pending.Decision.Task, Task.Delay(TimeSpan.FromSeconds(settings.ApprovalWaitSeconds), wait.Token)).ConfigureAwait(false);
        wait.Cancel();
        if (finished != pending.Decision.Task && pending.Decision.TrySetResult(ApprovalDecision.None))
            _dispatcher.TryEnqueue(() => RemoveApproval(pending));
        return await pending.Decision.Task.ConfigureAwait(false);
    }

    private void EnqueueApproval(PendingApproval pending)
    {
        if (_notificationsDisposed) { pending.Decision.TrySetResult(ApprovalDecision.None); return; }
        _approvals.Add(pending);
        var r = pending.Request;
        ApplyPayload(new AgentPayload
        {
            Agent = r.Agent, Status = AgentStatus.AwaitingApproval, Message = $"Allow {r.Tool}: {r.Detail}",
            Session = r.Session.Length > 0 ? r.Session : null, Cwd = r.Cwd.Length > 0 ? r.Cwd : null, Pid = r.Pid, Hwnd = r.Hwnd,
            ReceivedAt = DateTimeOffset.Now,
        });
        SyncApprovalCard();
    }

    private void RemoveApproval(PendingApproval pending)
    {
        if (_approvals.Remove(pending)) SyncApprovalCard();
    }

    private void Decide(ApprovalDecision decision)
    {
        if (_approvals.Count == 0) return;
        var pending = _approvals[0];
        _approvals.RemoveAt(0);
        if (pending.Decision.TrySetResult(decision) && decision != ApprovalDecision.None)
        {
            var r = pending.Request;
            ApplyPayload(new AgentPayload
            {
                Agent = r.Agent, Status = AgentStatus.Running, Message = (decision == ApprovalDecision.Allow ? "Allowed " : "Denied ") + r.Tool,
                Session = r.Session.Length > 0 ? r.Session : null, Cwd = r.Cwd.Length > 0 ? r.Cwd : null, ReceivedAt = DateTimeOffset.Now,
            });
        }
        SyncApprovalCard();
    }

    private void ResolveAllApprovals(ApprovalDecision decision)
    {
        foreach (var pending in _approvals.ToArray()) pending.Decision.TrySetResult(decision);
        _approvals.Clear();
        if (_ready) SyncApprovalCard();
    }

    private void SyncApprovalCard()
    {
        if (_approvals.Count == 0)
        {
            ApprovalCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            var r = _approvals[0].Request;
            ApprovalCard.Visibility = Visibility.Visible;
            ApprovalTitle.Text = $"{r.Agent} wants to use {r.Tool}";
            ApprovalDetail.Text = r.Detail;
            ApprovalMeta.Text = string.Join(" · ", new[] { r.Project, _approvals.Count > 1 ? $"1 of {_approvals.Count}" : "" }.Where(s => s.Length > 0));
        }
        UpdateUsagePanelHeight();
        UpdateBadges();
    }

    private void ApprovalAllow_Click(object sender, RoutedEventArgs e) => Decide(ApprovalDecision.Allow);
    private void ApprovalDeny_Click(object sender, RoutedEventArgs e) => Decide(ApprovalDecision.Deny);
    private void ApprovalTerminal_Click(object sender, RoutedEventArgs e) => Decide(ApprovalDecision.None);

    private void ApprovalFocus_Click(object sender, RoutedEventArgs e)
    {
        if (_approvals.Count > 0) WindowHelper.FocusSender(_approvals[0].Request.Hwnd, _approvals[0].Request.Pid);
    }
}
