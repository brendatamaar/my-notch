namespace MyNotch.Models;

public sealed record LyricsQuery(string Artist, string Title, TimeSpan Duration)
{
    public string Album { get; init; } = string.Empty;
}
public abstract record LyricsResult
{
    public sealed record Synced(IReadOnlyList<LrcLine> Lines) : LyricsResult;
    public sealed record Plain(string Text) : LyricsResult;
    public sealed record Instrumental : LyricsResult;
    public sealed record Missing : LyricsResult;
    public sealed record Failed(string Message, TimeSpan? RetryAfter = null, bool Transient = true) : LyricsResult;
}
public abstract record LyricsState(long Attempt)
{
    public sealed record Idle(long Generation = 0) : LyricsState(Generation);
    public sealed record Loading(long Generation, LyricsQuery Query, int Retries = 0) : LyricsState(Generation);
    public sealed record Waiting(long Generation, LyricsQuery Query, int Retries, LyricsResult Result) : LyricsState(Generation);
    public sealed record Finished(long Generation, LyricsResult Result) : LyricsState(Generation);
}
public abstract record LyricsInput
{
    public sealed record Request(LyricsQuery Query) : LyricsInput;
    public sealed record Reset : LyricsInput;
    public sealed record Completed(long Attempt, LyricsResult Result) : LyricsInput;
    public sealed record RetryDue(long Attempt) : LyricsInput;
}
public abstract record LyricsCommand(long Attempt);
public sealed record FetchLyrics(long Generation, LyricsQuery Query) : LyricsCommand(Generation);
public sealed record ScheduleLyricsRetry(long Generation, TimeSpan Delay) : LyricsCommand(Generation);

/// <summary>Pure lifecycle. Every retry/reset advances the generation; stale results are ignored.</summary>
public static class LyricsLookup
{
    public static (LyricsState State, LyricsCommand? Command) Step(LyricsState state, LyricsInput input) => input switch
    {
        LyricsInput.Request request => (new LyricsState.Loading(state.Attempt + 1, request.Query),
            new FetchLyrics(state.Attempt + 1, request.Query)),
        LyricsInput.Reset => (new LyricsState.Idle(state.Attempt + 1), null),
        LyricsInput.Completed completed when state is LyricsState.Loading loading && completed.Attempt == state.Attempt
            && RetryDelay(completed.Result, loading.Retries) is { } delay =>
            (new LyricsState.Waiting(state.Attempt, loading.Query, loading.Retries, completed.Result),
                new ScheduleLyricsRetry(state.Attempt, delay)),
        LyricsInput.Completed completed when state is LyricsState.Loading && completed.Attempt == state.Attempt =>
            (new LyricsState.Finished(state.Attempt, completed.Result), null),
        LyricsInput.RetryDue due when state is LyricsState.Waiting waiting && due.Attempt == state.Attempt =>
            (new LyricsState.Loading(state.Attempt + 1, waiting.Query, waiting.Retries + 1),
                new FetchLyrics(state.Attempt + 1, waiting.Query)),
        LyricsInput.RetryDue => (state, null),
        LyricsInput.Completed => (state, null),
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };
    public static TimeSpan? RetryDelay(LyricsResult result, int retries)
    {
        if (result is not (LyricsResult.Missing or LyricsResult.Failed { Transient: true })) return null;
        // A missing track is retried a few times (catalogue may lag or metadata settle); a transient
        // failure (offline, 5xx, rate limit) keeps retrying with a capped backoff until a song change cancels it.
        if (result is LyricsResult.Missing && retries >= 4) return null;
        var delay = TimeSpan.FromSeconds(retries switch { 0 => 2, 1 => 8, 2 => 15, 3 => 30, _ => 60 });
        return result is LyricsResult.Failed { RetryAfter: { } after } && after > delay ? after : delay;
    }
    public static LyricsState Run(LyricsState initial, IEnumerable<LyricsInput> inputs) =>
        inputs.Aggregate(initial, (state, input) => Step(state, input).State);
}
