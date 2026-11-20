using System.Globalization;
using System.Net;
using System.Text.Json;
using MyNotch.Models;
namespace MyNotch.Services;

/// <summary>LRCLIB adapter with positive caching, exact lookup and conservative search fallback.</summary>
public sealed class LyricsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();
    private readonly Dictionary<LyricsQuery, (LyricsResult Result, LinkedListNode<LyricsQuery> Node)> _cache = new();
    private readonly LinkedList<LyricsQuery> _recent = new();
    private DateTimeOffset _retryAfter;
    public LyricsService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyNotch/1.0");
    }
    /// <summary><paramref name="onPlainFallback"/> fires once, as soon as unsynced lyrics are
    /// known, while the remaining editions are still being checked for a synced copy.</summary>
    public async Task<LyricsResult> GetAsync(LyricsQuery query, CancellationToken ct, Action<LyricsResult>? onPlainFallback = null)
    {
        ct.ThrowIfCancellationRequested();
        query = query with { Title = LyricsMatching.CleanTitle(query.Title), Artist = query.Artist.Trim(),
            Album = query.Album.Trim(), Duration = TimeSpan.FromSeconds(Math.Round(Math.Max(0, query.Duration.TotalSeconds))) };
        if (string.IsNullOrWhiteSpace(query.Title)) return new LyricsResult.Missing();
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(query, out var cached))
            {
                _recent.Remove(cached.Node);
                _recent.AddFirst(cached.Node);
                return cached.Result;
            }
        }
        if (DateTimeOffset.UtcNow < _retryAfter) return Failure("Lyrics service is temporarily busy.");
        var title = LyricsMatching.CleanTitle(query.Title);
        var signature = $"artist_name={Uri.EscapeDataString(query.Artist)}&track_name={Uri.EscapeDataString(title)}";
        LyricsResult fallback = new LyricsResult.Missing();
        try
        {
            // Search reads LRCLIB's existing catalogue; /get can wait on external
            // providers. Prefer the fast catalogue path for seamless track switches.
            var search = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(LyricsMatching.PrimaryArtist(query.Artist))}";
            var candidates = await SearchAsync($"https://lrclib.net/api/search?{search}", ct).ConfigureAwait(false);
            var found = LyricsMatching.Decode(LyricsMatching.Select(candidates, query), query.Duration);
            if (found is LyricsResult.Synced or LyricsResult.Instrumental) return Remember(query, found, ct);
            if (found is LyricsResult.Plain)
            {
                fallback = found;
                onPlainFallback?.Invoke(found);
            }
            var urls = new List<string>();
            var albumParameter = string.IsNullOrWhiteSpace(query.Album) ? string.Empty : $"&album_name={Uri.EscapeDataString(query.Album)}";
            if (query.Duration.TotalSeconds is >= 1 and <= 3600)
            {
                var durationParameter = $"&duration={Math.Round(query.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)}";
                if (albumParameter.Length > 0) urls.Add($"https://lrclib.net/api/get?{signature}{albumParameter}{durationParameter}");
                urls.Add($"https://lrclib.net/api/get?{signature}{durationParameter}");
            }
            urls.Add($"https://lrclib.net/api/get?{signature}");
            // Remaining editions run concurrently and the first synced copy wins: running them
            // one after another made every track switch wait for the sum of all of them.
            var freeText = Uri.EscapeDataString($"{LyricsMatching.PrimaryArtist(query.Artist)} {title}".Trim());
            var attempts = new List<Func<CancellationToken, Task<LyricsResult>>>
            {
                // Free-text search tolerates artist/title spelling differences the structured search misses.
                async token => LyricsMatching.Decode(LyricsMatching.Select(
                    await SearchAsync($"https://lrclib.net/api/search?q={freeText}", token).ConfigureAwait(false), query), query.Duration),
            };
            attempts.AddRange(urls.Select<string, Func<CancellationToken, Task<LyricsResult>>>(url =>
                async token => LyricsMatching.Decode(await FetchAsync(url, token).ConfigureAwait(false), query.Duration)));
            using var parallel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = attempts.Select(attempt => RunAttempt(attempt, parallel.Token, ct)).ToList();
            Exception? firstError = null;
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var (result, error) = await done.ConfigureAwait(false);
                if (error is not null) { firstError ??= error; continue; }
                if (result is LyricsResult.Synced or LyricsResult.Instrumental)
                {
                    parallel.Cancel();
                    return Remember(query, result, ct);
                }
                if (result is LyricsResult.Plain && fallback is not LyricsResult.Plain)
                {
                    fallback = result;
                    onPlainFallback?.Invoke(result);
                }
            }
            if (fallback is LyricsResult.Plain) return Remember(query, fallback, ct);
            if (firstError is not null) throw firstError;
            return Remember(query, fallback, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Lyrics lookup failed: {ex.Message}");
            return fallback is LyricsResult.Plain ? Remember(query, fallback, ct)
                : Failure("Unable to load lyrics right now.", IsTransient(ex));
        }
    }
    private static async Task<(LyricsResult? Result, Exception? Error)> RunAttempt(
        Func<CancellationToken, Task<LyricsResult>> attempt, CancellationToken token, CancellationToken caller)
    {
        try { return (await attempt(token).ConfigureAwait(false), null); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, null); } // a sibling already found synced lyrics
        catch (Exception ex) { return (null, ex); }
    }
    private LyricsResult Remember(LyricsQuery query, LyricsResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (result is LyricsResult.Synced or LyricsResult.Plain or LyricsResult.Instrumental)
        {
            lock (_cacheLock)
            {
                if (_cache.Remove(query, out var old)) _recent.Remove(old.Node);
                var node = _recent.AddFirst(query);
                _cache[query] = (result, node);
                if (_cache.Count > 32 && _recent.Last is { } last)
                {
                    _cache.Remove(last.Value);
                    _recent.RemoveLast();
                }
            }
        }
        return result;
    }
    private LyricsResult.Failed Failure(string message, bool transient = true) =>
        new(message, _retryAfter > DateTimeOffset.UtcNow ? _retryAfter - DateTimeOffset.UtcNow : null, transient);
    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } status })
            return (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
        return true;
    }
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            _retryAfter = response.Headers.RetryAfter?.Date
                ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));
        return response;
    }
    private async Task<LrclibResponse?> FetchAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, LrclibJsonContext.Default.LrclibResponse);
    }
    private async Task<LrclibResponse[]> SearchAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, LrclibJsonContext.Default.LrclibResponseArray) ?? [];
    }
    public void Dispose() => _http.Dispose();
}
