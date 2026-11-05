using System.Globalization;
using Microsoft.UI.Dispatching;
using MyNotch.Models;

namespace MyNotch.Services;

public sealed class PrayerTimesService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _placeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", "prayer-location.txt");
    private DispatcherQueueTimer? _timer;
    private DispatcherQueueTimer? _retryTimer;
    private int _failures;
    private DateTime _todayDate;
    private int _generation;
    private bool _disposed, _loading;
    public PrayerLocation Location { get; private set; } = PrayerLocation.Places[0];
    public PrayerTimes? Today { get; private set; }
    public string? NextPrayerName { get; private set; }
    public DateTime? NextPrayerTime { get; private set; }
    public DateTime? NextCityTime { get; private set; }
    public string Status { get; private set; } = "Loading prayer times...";
    public event EventHandler? ScheduleChanged;
    public event EventHandler<string>? ReminderDue;
    public PrayerTimesService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try { var id = File.ReadAllText(_placeFile).Trim(); Location = PrayerLocation.Places.FirstOrDefault(p => p.Id == id) ?? Location; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public Task StartAsync() => RefreshAsync();
    public Task RefreshIfStaleAsync() => !_loading && (Today is null || _todayDate != Location.LocalNow(DateTimeOffset.Now).Date || NextPrayerTime <= DateTime.Now) ? RefreshAsync() : Task.CompletedTask;
    public Task SelectLocationAsync(PrayerLocation place)
    {
        if (Location == place && Today is not null) return Task.CompletedTask;
        Location = place;
        try { Directory.CreateDirectory(Path.GetDirectoryName(_placeFile)!); File.WriteAllText(_placeFile, place.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        return RefreshAsync();
    }
    public void Dispose() { _disposed = true; _generation++; _timer?.Stop(); _retryTimer?.Stop(); _http.Dispose(); }
    private async Task<PrayerTimes> FetchAsync(PrayerLocation place, DateTime date)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"https://api.aladhan.com/v1/timings/{date:dd-MM-yyyy}?latitude={place.Latitude}&longitude={place.Longitude}&method=20&timezonestring={place.ApiZone}");
        var json = await _http.GetStringAsync(url).ConfigureAwait(false);
        return PrayerTimes.ParseDay(json) ?? throw new InvalidOperationException("Invalid prayer schedule");
    }
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        int version = ++_generation;
        var place = Location;
        var now = place.LocalNow(DateTimeOffset.Now);
        _loading = true;
        _timer?.Stop();
        Today = null; NextPrayerTime = null; NextCityTime = null; NextPrayerName = null;
        Status = "Loading prayer times...";
        ScheduleChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var day = await FetchAsync(place, now.Date);
            var fetchedDate = now.Date;
            now = place.LocalNow(DateTimeOffset.Now);
            if (now.Date != fetchedDate) day = await FetchAsync(place, now.Date);
            var next = day.GetNext(now);
            if (next.At.Date > now.Date)
            {
                var tomorrow = await FetchAsync(place, now.Date.AddDays(1));
                next = ("Fajr", now.Date.AddDays(1) + tomorrow.Fajr.ToTimeSpan());
            }
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || version != _generation) return;
                Today = day; _todayDate = now.Date;
                NextPrayerName = next.Name; NextCityTime = next.At;
                NextPrayerTime = place.ToMachineTime(next.At);
                Status = "Kemenag method | Aladhan";
                _timer ??= CreateTimer();
                var delay = NextPrayerTime.Value - DateTime.Now;
                _timer.Interval = delay > TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
                _timer.Start();
                _failures = 0; _retryTimer?.Stop();
                _loading = false;
                ScheduleChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || version != _generation) return;
                _loading = false;
                // Without a schedule no reminder is armed, so retry on our own (1, 2, 4, 8, then every 15 min).
                var wait = TimeSpan.FromMinutes(Math.Min(15, 1 << Math.Min(_failures++, 4)));
                Status = $"Schedule unavailable - retrying in {wait.TotalMinutes:0} min";
                if (_retryTimer is null)
                {
                    _retryTimer = _dispatcher.CreateTimer();
                    _retryTimer.IsRepeating = false;
                    _retryTimer.Tick += (_, _) => { if (!_disposed && !_loading && Today is null) _ = RefreshAsync(); };
                }
                _retryTimer.Interval = wait;
                _retryTimer.Start();
                ScheduleChanged?.Invoke(this, EventArgs.Empty);
            });
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Prayer: {ex.Message}");
        }
    }
    private DispatcherQueueTimer CreateTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { ReminderDue?.Invoke(this, PrayerPresentation.DisplayName(NextPrayerName)); _ = RefreshAsync(); };
        return timer;
    }
}
