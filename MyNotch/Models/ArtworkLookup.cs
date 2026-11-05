namespace MyNotch.Models;

public abstract record ArtworkState(long Attempt, string Key, byte[]? Bytes)
{
    public sealed record Idle(long Generation = 0) : ArtworkState(Generation, string.Empty, null);
    public sealed record Loading(long Generation, string TrackKey, byte[]? Previous) : ArtworkState(Generation, TrackKey, Previous);
    public sealed record Ready(long Generation, string TrackKey, byte[] Image) : ArtworkState(Generation, TrackKey, Image);
    public sealed record Missing(long Generation, string TrackKey) : ArtworkState(Generation, TrackKey, null);
}

public abstract record ArtworkInput
{
    public sealed record Request(string Key, bool Refresh) : ArtworkInput;
    public sealed record Completed(long Attempt, byte[]? Bytes) : ArtworkInput;
    public sealed record Reset : ArtworkInput;
}

public sealed record FetchArtwork(long Attempt);

/// <summary>Pure artwork cache/lifecycle. Updated media properties force a read, even for the same song.</summary>
public static class ArtworkLookup
{
    public static ArtworkState Run(ArtworkState initial, IEnumerable<ArtworkInput> inputs) =>
        inputs.Aggregate(initial, (state, input) => Step(state, input).State);

    public static (ArtworkState State, FetchArtwork? Command) Step(ArtworkState state, ArtworkInput input) => input switch
    {
        ArtworkInput.Request request when state is ArtworkState.Ready && state.Key == request.Key && !request.Refresh => (state, null),
        ArtworkInput.Request request => (
            new ArtworkState.Loading(state.Attempt + 1, request.Key, state.Key == request.Key ? state.Bytes : null),
            new FetchArtwork(state.Attempt + 1)),
        ArtworkInput.Completed completed when state is ArtworkState.Loading && state.Attempt == completed.Attempt =>
            (completed.Bytes is null ? new ArtworkState.Missing(state.Attempt, state.Key)
                : new ArtworkState.Ready(state.Attempt, state.Key, completed.Bytes), null),
        ArtworkInput.Completed => (state, null),
        ArtworkInput.Reset => (new ArtworkState.Idle(state.Attempt + 1), null),
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };
}
