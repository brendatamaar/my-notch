using Microsoft.UI.Dispatching;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// Next-meeting tracker over a published ICS feed. The feed is fetched at start, when the URL changes and every
/// 30 minutes while a URL is set; between fetches one one-shot timer is armed for the next boundary
/// (alert time, start, end). UI-thread only; events fire on the UI thread.
/// </summary>
public sealed class MeetingService : IDisposable
{
    private static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(36);
    private const long MaxFeedBytes = 8 * 1024 * 1024;

    private readonly DispatcherQueue _dispatcher;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DispatcherQueueTimer _fetchTimer, _boundaryTimer;
    private readonly HashSet<string> _alerted = new();
    private IReadOnlyList<IcsEvent> _events = Array.Empty<IcsEvent>();
    private string _url = "";
    private TimeSpan _lead = TimeSpan.FromMinutes(10);
    private int _generation;
    private bool _disposed;

    public Meeting? Next { get; private set; }
    public string Status { get; private set; } = "No calendar feed set";
    public TimeSpan Lead => _lead;

    /// <summary><see cref="Next"/> or <see cref="Status"/> changed.</summary>
    public event EventHandler? Changed;
    /// <summary>A meeting is <see cref="Lead"/> away (raised once per meeting).</summary>
    public event EventHandler<Meeting>? MeetingSoon;

    public MeetingService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyNotch/1.0");
        _fetchTimer = dispatcher.CreateTimer(); _fetchTimer.IsRepeating = true; _fetchTimer.Interval = FetchEvery;
        _fetchTimer.Tick += (_, _) => _ = FetchAsync();
        _boundaryTimer = dispatcher.CreateTimer(); _boundaryTimer.IsRepeating = false;
        _boundaryTimer.Tick += (_, _) => Recompute();
    }

    public void Configure(string url, TimeSpan lead)
    {
        _lead = lead;
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (url == _url) { Recompute(); return; }
        _url = url;
        _events = Array.Empty<IcsEvent>();
        _alerted.Clear();
        if (_url.Length == 0)
        {
            _fetchTimer.Stop(); _boundaryTimer.Stop();
            Next = null; Status = "No calendar feed set";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!Uri.TryCreate(_url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _fetchTimer.Stop(); _boundaryTimer.Stop();
            Next = null; Status = "Calendar feed must be an https:// or webcal:// address";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _fetchTimer.Start();
        _ = FetchAsync();
    }

    public Task RefreshAsync() => _url.Length == 0 ? Task.CompletedTask : FetchAsync();

    private async Task FetchAsync()
    {
        if (_disposed || _url.Length == 0) return;
        int generation = ++_generation;
        string url = _url;
        Status = "Loading calendar…";
        Changed?.Invoke(this, EventArgs.Empty);
        IReadOnlyList<IcsEvent>? events = null;
        string status;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new InvalidDataException("Calendar feed is too large");
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > MaxFeedBytes) throw new InvalidDataException("Calendar feed is too large");
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("That address isn't an iCalendar feed");
            events = await Task.Run(() => IcsCalendar.Parse(text)).ConfigureAwait(false);
            status = $"Calendar updated {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            status = ex is InvalidDataException ? ex.Message : "Calendar feed unavailable; retrying in 30 min";
        }
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || generation != _generation || url != _url) return;
            if (events is not null) _events = events;
            Status = status;
            Recompute();
        });
    }

    private void Recompute()
    {
        if (_disposed) return;
        _boundaryTimer.Stop();
        var now = DateTimeOffset.Now;
        var upcoming = IcsCalendar.Occurrences(_events, now, now + Horizon);
        Next = upcoming.FirstOrDefault(m => m.End > now);

        // Alert once when a meeting enters its lead window (or immediately if MyNotch started inside it).
        if (Next is { } next && next.Start - _lead <= now && next.Start + TimeSpan.FromMinutes(5) > now && _alerted.Add(Key(next)))
            MeetingSoon?.Invoke(this, next);
        if (_alerted.Count > 200) _alerted.Clear();

        // Arm the nearest future boundary of the next two meetings.
        DateTimeOffset? boundary = null;
        foreach (var m in upcoming.Take(2))
            foreach (var at in new[] { m.Start - _lead, m.Start, m.Start + TimeSpan.FromMinutes(5), m.End })
                if (at > now && (boundary is null || at < boundary)) boundary = at;
        if (boundary is { } b)
        {
            var wait = b - now;
            _boundaryTimer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : wait;
            _boundaryTimer.Start();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Key(Meeting m) => m.Title + "\u001f" + m.Start.UtcTicks;

    public void Dispose()
    {
        _disposed = true;
        _fetchTimer.Stop(); _boundaryTimer.Stop();
        _http.Dispose();
    }
}
