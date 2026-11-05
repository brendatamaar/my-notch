using MyNotch.Models;
using MyNotch.Services;
if (args.Length >= 4 && args[0] == "--lookup")
{
    var captured = new LyricsQuery(args[1], args[2], TimeSpan.FromSeconds(double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)))
        {Album = args.Length > 4 ? args[4] : string.Empty};
    using var lookup = new LyricsService();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var found = await lookup.GetAsync(captured, default);
    Console.WriteLine($"First lookup: {watch.ElapsedMilliseconds} ms");
    watch.Restart();
    var cached = await lookup.GetAsync(captured, default);
    Console.WriteLine($"Cached lookup: {watch.ElapsedMilliseconds} ms ({cached.GetType().Name})");
    Console.WriteLine("Captured metadata lookup: " + found.GetType().Name);
    if (found is LyricsResult.Synced lines) Console.WriteLine("Timed lines: " + lines.Lines.Count);
    if (found is LyricsResult.Failed failure) Console.WriteLine(failure.Message);
    if (found is not LyricsResult.Synced) Environment.ExitCode = 2;
    return;
}
using var media = new MediaControlService();
var ready = new TaskCompletionSource<MediaTrackInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
media.TrackChanged += (_, track) => {
    if (args.Contains("--artwork")) {
        var fingerprint = track.Thumbnail is null ? "pending/none" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(track.Thumbnail))[..12];
        Console.WriteLine($"revision={track.Revision} title={track.Title} artwork={fingerprint} bytes={track.Thumbnail?.Length ?? 0}");
    }
    if(track.HasSession && !string.IsNullOrWhiteSpace(track.Title)) ready.TrySetResult(track);
};
await media.StartAsync();
MediaTrackInfo song;
try { song = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
catch(TimeoutException) { Console.WriteLine("No active media metadata available"); return; }
Console.WriteLine($"Title: {song.Title}\nArtist: {song.Artist}\nAlbum: {song.Album}\nProvider: {MediaMetadata.ProviderName(song.SourceApp)}\nSource: {song.SourceApp}\nDuration: {song.Timeline.Duration.TotalSeconds}\nPlaying: {song.IsPlaying}");
if (args.Contains("--artwork")) { await Task.Delay(TimeSpan.FromSeconds(3)); return; }
using var lyrics = new LyricsService();
var result = await lyrics.GetAsync(new LyricsQuery(song.Artist, song.Title, song.Timeline.Duration) { Album = song.Album }, default);
Console.WriteLine("Lyrics result: " + result.GetType().Name);
if (result is LyricsResult.Failed failed) Console.WriteLine(failed.Message);
if (result is LyricsResult.Synced synced) Console.WriteLine("Timed lines: " + synced.Lines.Count);
