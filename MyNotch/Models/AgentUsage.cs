namespace MyNotch.Models;

public enum UsageProvider { Codex, Claude, Antigravity, Muse }
public enum UsageProblem { SignInRequired, NotInstalled, NoData, Offline, RateLimited, Unsupported, InvalidData }
public sealed record UsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt);
public sealed record UsageResetCredit(string Id, string Title, DateTimeOffset? ExpiresAt);
public sealed record UsageSnapshot(UsageProvider Provider, string? Plan, DateTimeOffset AsOf, IReadOnlyList<UsageWindow> Windows,
    int AvailableResets = 0, IReadOnlyList<UsageResetCredit>? ResetCredits = null);
public enum UsageResetOutcome { Reset, AlreadyRedeemed, NothingToReset, NoCredit, Failed, Unknown }
public static class UsageReset
{
    // Stable opaque filename key for the per-credit request journal.
    public static string Key(string creditId) => new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("my-notch-reset:" + creditId)).AsSpan(0, 16)).ToString();
}
public sealed record UsageOutcome(UsageSnapshot? Snapshot, UsageProblem? Problem, DateTimeOffset? RetryAfter = null);

public abstract record UsageContent
{
    public sealed record Waiting : UsageContent;
    public sealed record Loading(UsageSnapshot? Previous) : UsageContent;
    public sealed record Ready(UsageSnapshot Snapshot) : UsageContent;
    public sealed record Failed(UsageProblem Problem, UsageSnapshot? Previous) : UsageContent;
}
public sealed record UsageLoad(int Generation, UsageContent Content)
{
    public static UsageLoad Initial => new(0, new UsageContent.Waiting());
    public UsageSnapshot? Snapshot => Content switch
    {
        UsageContent.Loading x => x.Previous,
        UsageContent.Ready x => x.Snapshot,
        UsageContent.Failed x => x.Previous,
        _ => null,
    };
    public UsageLoad Begin() => new(Generation + 1, new UsageContent.Loading(Snapshot));
    public UsageLoad Complete(int generation, UsageOutcome outcome) => generation != Generation || Content is not UsageContent.Loading
        ? this
        : new(Generation, outcome.Snapshot is { } snapshot ? new UsageContent.Ready(snapshot) : new UsageContent.Failed(outcome.Problem ?? UsageProblem.InvalidData, Snapshot));
}

public static class UsagePresentation
{
    public static string Name(UsageProvider provider) => provider switch
    { UsageProvider.Codex => "Codex", UsageProvider.Claude => "Claude Code", UsageProvider.Antigravity => "Antigravity", UsageProvider.Muse => "Muse Code", _ => throw new ArgumentOutOfRangeException(nameof(provider)) };
    public static string Problem(UsageProvider provider, UsageProblem problem) => problem switch
    {
        UsageProblem.SignInRequired => provider == UsageProvider.Claude ? "Sign in again in Claude Code" : "Sign in again in the provider CLI",
        UsageProblem.NotInstalled => "CLI not found on this computer",
        UsageProblem.NoData => provider == UsageProvider.Muse ? "Server has no allowance data yet. Retry after your next Muse session." : "No quota data reported by the provider",
        UsageProblem.Offline => "Could not reach provider. Try Refresh later.",
        UsageProblem.RateLimited => "Usage check rate-limited. Try again later.",
        UsageProblem.Unsupported => "CLI version or account does not expose usage",
        UsageProblem.InvalidData => "Provider returned an unrecognized usage format",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };
    public static string Reset(DateTimeOffset? at, DateTimeOffset now) => at is null ? "Reset not reported"
        : at <= now ? "Reset passed - refresh needed" : $"resets {at.Value.LocalDateTime:ddd HH:mm}";
}
