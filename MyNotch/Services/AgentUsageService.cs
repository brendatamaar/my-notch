using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Bounded per-provider refreshes, single-flight, cooldowns and shutdown fencing.</summary>
public sealed class AgentUsageService : IDisposable
{
    private readonly IAgentUsageReader _reader;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<UsageProvider, UsageLoad> _states = Enum.GetValues<UsageProvider>().ToDictionary(p => p, _ => UsageLoad.Initial);
    private readonly Dictionary<UsageProvider, DateTimeOffset> _next = new();
    private readonly HashSet<UsageProvider> _enabled = Enum.GetValues<UsageProvider>().ToHashSet();
    private bool _disposed;
    public event EventHandler? Changed;
    public bool IsResetting { get; private set; }
    public string ResetStatus { get; private set; } = "";
    public AgentUsageService(IAgentUsageReader reader, Func<DateTimeOffset>? now = null) { _reader = reader; _now = now ?? (() => DateTimeOffset.UtcNow); }
    public UsageLoad GetState(UsageProvider provider) { lock (_gate) return _states[provider]; }
    public bool IsEnabled(UsageProvider provider) { lock (_gate) return _enabled.Contains(provider); }
    public void SetEnabled(UsageProvider provider, bool enabled) { lock (_gate) { if (enabled) _enabled.Add(provider); else _enabled.Remove(provider); } Changed?.Invoke(this, EventArgs.Empty); }
    public Task RefreshAsync() => Task.WhenAll(Enum.GetValues<UsageProvider>().Select(provider => RefreshProviderAsync(provider)));
    private async Task RefreshProviderAsync(UsageProvider provider, bool afterReset = false)
    {
        int generation;
        lock (_gate)
        {
            if (_disposed || (provider == UsageProvider.Codex && IsResetting && !afterReset) || (!_enabled.Contains(provider) && !afterReset) || _states[provider].Content is UsageContent.Loading || _next.GetValueOrDefault(provider) > _now()) return;
            _states[provider] = _states[provider].Begin();
            generation = _states[provider].Generation;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        UsageOutcome result;
        try { result = await _reader.ReadAsync(provider, _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { return; }
        // Any other failure must still complete the load: a provider left in Loading is never refreshed again.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Usage read failed for {provider}: {ex}");
            result = new(null, UsageProblem.Offline);
        }
        lock (_gate)
        {
            if (_disposed) return;
            _states[provider] = _states[provider].Complete(generation, result);
            TimeSpan gap = result.Problem is not null ? TimeSpan.FromSeconds(30) : provider switch
            { UsageProvider.Antigravity => TimeSpan.FromMinutes(15), UsageProvider.Muse => TimeSpan.FromMinutes(5), _ => TimeSpan.FromMinutes(2) };
            if (result.Problem == UsageProblem.RateLimited) gap = TimeSpan.FromMinutes(5);
            if (provider == UsageProvider.Antigravity) gap = TimeSpan.FromMinutes(15);
            _next[provider] = result.RetryAfter is { } retry && retry > _now().Add(gap) ? retry : _now().Add(gap);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task ResetCodexAsync()
    {
        UsageResetCredit? credit;
        lock (_gate)
        {
            if (_disposed || IsResetting || _reader is not IUsageResetter || _states[UsageProvider.Codex].Content is not UsageContent.Ready ready || ready.Snapshot.AvailableResets <= 0) return;
            credit = ready.Snapshot.ResetCredits?.FirstOrDefault(c => c.ExpiresAt is null || c.ExpiresAt > _now());
            if (credit is null) return;
            IsResetting = true; ResetStatus = "Resetting Codex limit...";
        }
        Changed?.Invoke(this, EventArgs.Empty);
        var outcome = await ((IUsageResetter)_reader).ResetCodexAsync(credit, _shutdown.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            ResetStatus = outcome switch
            {
                UsageResetOutcome.Reset => "Codex limit reset. Updating usage...",
                UsageResetOutcome.AlreadyRedeemed => "This reset was already used. Updating usage...",
                UsageResetOutcome.NoCredit => "No reset credits available.",
                UsageResetOutcome.NothingToReset => "Codex has no eligible limit to reset.",
                UsageResetOutcome.Unknown => "Reset result could not be confirmed. Updating usage...",
                _ => "Reset was not completed. Updating usage...",
            };
            _next.Remove(UsageProvider.Codex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshProviderAsync(UsageProvider.Codex, afterReset: true).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            IsResetting = false;
            ResetStatus = outcome switch
            {
                UsageResetOutcome.Reset => "Codex limit reset.",
                UsageResetOutcome.AlreadyRedeemed => "This reset was already redeemed.",
                UsageResetOutcome.NoCredit => "No reset credits available.",
                UsageResetOutcome.NothingToReset => "Codex has no eligible limit to reset.",
                UsageResetOutcome.Unknown => "Reset result is unconfirmed. Check the current usage reading.",
                _ => "Reset request was not completed.",
            };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _shutdown.Cancel();
        if (_reader is IDisposable disposable) disposable.Dispose();
        // Tasks may still be observing the cancellation token; no new work can start.
    }
}
