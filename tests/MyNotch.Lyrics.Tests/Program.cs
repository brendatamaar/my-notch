using System.Net;
using System.Text.Json;
using MyNotch.Models;
using MyNotch.Services;

NotificationTests.Run();
WellnessTests.Run();
FeatureTests.Run();
if (OperatingSystem.IsWindows()) await FeatureTests.RunHttpAsync();

if (args.Contains("--dev-port-probe") && OperatingSystem.IsWindows())
{
    string project=Path.Combine(Environment.CurrentDirectory,"tests",".dev server test "+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(project);File.WriteAllText(Path.Combine(project,"pyproject.toml"),"[project]\nname = \"port-probe\"\nversion = \"0.1.0\"\n");
    try
    {
        foreach(string address in new[]{"127.0.0.1","0.0.0.0","::1"})
        {
            var start=new System.Diagnostics.ProcessStartInfo(@"C:\Python312\python.exe"){WorkingDirectory=project,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"-u","-m","http.server","0","--bind",address})start.ArgumentList.Add(argument);
            using var server=System.Diagnostics.Process.Start(start)!;
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var line=await server.StandardOutput.ReadLineAsync(timeout.Token);
                if(line is null)throw new Exception("Test HTTP server did not start");
                var process=MyNotch.Interop.DevProcessInspector.Read(server.Id);
                if(process is null || !process.WorkingDirectory.TrimEnd('\\').Equals(project,StringComparison.OrdinalIgnoreCase))throw new Exception("Native project directory lookup failed");
                var monitor=new PortMonitorService();
                var match=monitor.Refresh().SingleOrDefault(p=>p.Pid==server.Id)??throw new Exception("Project dev server not detected");
                if(match.Port!=int.Parse(System.Text.RegularExpressions.Regex.Match(line,"port ([0-9]+)").Groups[1].Value))throw new Exception("TCP port byte order mismatch");
                if(match.Framework!="Python server" || match.Created==0 || !match.ProjectPath.Equals(project,StringComparison.OrdinalIgnoreCase))throw new Exception($"Wrong project server identity: framework={match.Framework}, created={match.Created>0}, root={Path.GetFileName(match.ProjectPath)}, expected={Path.GetFileName(project)}");
                if(monitor.TryStop(match with {Created=match.Created+1},out _))throw new Exception("Stale process identity was accepted");
                if(!monitor.TryStop(match,out var stopError))throw new Exception("Own test server stop failed: "+stopError);
                await server.WaitForExitAsync(timeout.Token);
                if(monitor.Refresh().Any(p=>p.Pid==server.Id))throw new Exception("Exited server still listed");
                Console.WriteLine($"PASS: {address} project dev server discovered, stale identity rejected, own server stopped");
            }
            finally {if(!server.HasExited){server.Kill();await server.WaitForExitAsync();}}
        }
    }
    finally{File.Delete(Path.Combine(project,"pyproject.toml"));Directory.Delete(project);}
    return;
}

if (args.Contains("--system-dashboard-probe") && OperatingSystem.IsWindows())
{
    using var systemReader=new SystemUsageReader();
    var first=systemReader.Capture(true) ?? throw new Exception("No first system snapshot");
    await Task.Delay(1200);
    var live=systemReader.Capture(false) ?? throw new Exception("No second system snapshot");
    if(live.CpuPercent is null || live.MemoryTotal is null || live.Drives.Count==0 || live.Processes.Count==0)throw new Exception("Missing native system dashboard data");
    Console.WriteLine($"PASS: system dashboard CPU {live.CpuPercent:0.#}%, GPU {live.GpuPercent?.ToString("0.#") ?? "unreported"}%, {live.ProcessCount} processes, {live.Drives.Count} drives, {live.Adapters.Count} links, power={live.Power is not null}");
    return;
}

if (args.Contains("--system-probe"))
{
    var memory = new MyNotch.Interop.NativeMethods.MemoryStatusEx { Length = 64 };
    if (!MyNotch.Interop.NativeMethods.GlobalMemoryStatusEx(ref memory) || memory.TotalPhysical == 0 || memory.AvailablePhysical > memory.TotalPhysical) throw new Exception("Native memory sample failed");
    if (!MyNotch.Interop.NativeMethods.GetSystemTimes(out var idle1, out var kernel1, out var user1)) throw new Exception("CPU baseline failed");
    await Task.Delay(1000);
    if (!MyNotch.Interop.NativeMethods.GetSystemTimes(out var idle2, out var kernel2, out var user2)) throw new Exception("CPU sample failed");
    var cpu = SystemUsageMath.CpuPercent(new(idle1.Ticks, kernel1.Ticks, user1.Ticks), new(idle2.Ticks, kernel2.Ticks, user2.Ticks));
    if (cpu is null || cpu < 0 || cpu > 100) throw new Exception("Native CPU percentage invalid");
    Console.WriteLine($"PASS: native CPU {cpu:0.#}%, RAM {SystemUsageMath.Bytes(memory.TotalPhysical)}, uptime available {MyNotch.Interop.NativeMethods.GetTickCount64() > 0}");
    return;
}

if (args.Contains("--usage-probe"))
{
    using var liveReader = new AgentUsageReader();
    using var liveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var reports = await Task.WhenAll(Enum.GetValues<UsageProvider>().Select(async provider => (Provider: provider, Result: await liveReader.ReadAsync(provider, liveTimeout.Token))));
    foreach (var report in reports)
    {
        Console.WriteLine($"{UsagePresentation.Name(report.Provider)}: {report.Result.Problem?.ToString() ?? "Ready"}");
        if (report.Result.Snapshot is { } resetInfo && report.Provider == UsageProvider.Codex) Console.WriteLine($"  Reset credits available: {resetInfo.AvailableResets}");
        if (report.Result.Snapshot is { } sample) foreach (var row in sample.Windows) Console.WriteLine($"  {row.Label}: {row.UsedPercent:0.#}% used; {UsagePresentation.Reset(row.ResetsAt, DateTimeOffset.Now)}");
    }
    return;
}

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
var query = new LyricsQuery("Artist", "Song (Official Video)", TimeSpan.FromSeconds(180));
string Record(string? synced = null, string? plain = null, string artist = "Artist", double duration = 180) =>
    JsonSerializer.Serialize(new { trackName = "Song", artistName = artist, duration, syncedLyrics = synced, plainLyrics = plain });

// A catalogue hit should be a single fast request, not several slow exact lookups.
var handler = new FakeHandler((i, request) => new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent("[" + Record("[00:01.00]wrong", artist:"Other") + "," + Record("[00:01.00]right") + "]") });
using (var service = new LyricsService(handler)) {
    var result = await service.GetAsync(query, default);
    Check(result is LyricsResult.Synced s && s.Lines[0].Text == "right", "catalogue selection rejects wrong artist");
    Check(handler.Calls == 1 && handler.Urls[0].Contains("/search?"), "search first for fast track switches");
    Check(handler.Urls[0].Contains("track_name=Song&artist_name=Artist"), "clean query metadata");
    await service.GetAsync(query, default);
    Check(handler.Calls == 1, "positive cache");
}
handler = new FakeHandler((i, request) => new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("search") ? "[" + Record(plain:"unsynced lyrics") + "]" : Record(plain:"unsynced lyrics")) });
using (var service = new LyricsService(handler)) {
    Check(await service.GetAsync(query, default) is LyricsResult.Plain { Text: "unsynced lyrics" }, "preserve plain fallback");
    Check(handler.Calls == 4, "plain search still tries exact synced editions");
    await service.GetAsync(query, default);
    Check(handler.Calls == 4, "plain lyrics available immediately on returning to a song");
}
handler = new FakeHandler((i, request) => new HttpResponseMessage(i == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
    Content = new StringContent("[" + Record("[00:01.00]recovered") + "]") });
using (var service = new LyricsService(handler)) {
    Check(await service.GetAsync(query, default) is LyricsResult.Failed {RetryAfter: not null}, "transient failure includes cooldown");
    Check(await service.GetAsync(query, default) is LyricsResult.Failed && handler.Calls == 1, "honor server cooldown");
}
handler = new FakeHandler((i, request) => new HttpResponseMessage(i == 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) {
    Content = new StringContent("[" + Record("[00:01.00]recovered") + "]") });
using (var service = new LyricsService(handler)) {
    Check(await service.GetAsync(query, default) is LyricsResult.Failed, "HTTP error");
    Check(await service.GetAsync(query, default) is LyricsResult.Synced, "automatic retry can recover; failure isn't cached");
}
handler = new FakeHandler((i, request) => new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent("[{\"trackName\":\"Song\",\"artistName\":\"Artist\",\"duration\":180,\"instrumental\":true}]") });
using (var service = new LyricsService(handler)) {
    Check(await service.GetAsync(query, default) is LyricsResult.Instrumental && handler.Calls == 1, "instrumental is terminal");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await service.GetAsync(query, cancellation.Token); throw new Exception("cancellation swallowed"); }
    catch (OperationCanceledException) { }
}
handler = new FakeHandler((i, request) => {
    var title = Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?').Split('&').Single(p=>p.StartsWith("track_name=")).Split('=',2)[1]);
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] {
        new {trackName=title,artistName="Artist",duration=180,syncedLyrics="[00:01.00]cached line"}})) };
});
using (var service = new LyricsService(handler)) {
    var a = query with {Title="Track A"}; var b = query with {Title="Track B"};
    await service.GetAsync(a, default); await service.GetAsync(b, default); await service.GetAsync(a, default);
    Check(handler.Calls == 2, "A -> B -> A reuses lyrics without network");
    for(int n=0;n<33;n++) await service.GetAsync(query with {Title="Cache " + n}, default);
    int count=handler.Calls; await service.GetAsync(a, default);
    Check(handler.Calls == count + 1, "bounded cache evicts old songs");
}
var firstRequest = LyricsLookup.Step(new LyricsState.Idle(), new LyricsInput.Request(query));
var waiting = LyricsLookup.Step(firstRequest.State, new LyricsInput.Completed(firstRequest.State.Attempt,
    new LyricsResult.Failed("busy", TimeSpan.FromSeconds(30))));
Check(waiting.State is LyricsState.Waiting && waiting.Command is ScheduleLyricsRetry {Delay.TotalSeconds:30}, "automatically wait for server cooldown");
var switched = LyricsLookup.Step(waiting.State, new LyricsInput.Request(query with {Title="New song"}));
Check(ReferenceEquals(LyricsLookup.Step(switched.State, new LyricsInput.RetryDue(waiting.State.Attempt)).State, switched.State),
    "old song retry cannot replace a new song");
var automatic = LyricsLookup.Step(waiting.State, new LyricsInput.RetryDue(waiting.State.Attempt));
Check(automatic.Command is FetchLyrics && automatic.State.Attempt > waiting.State.Attempt, "automatic retry starts a fresh fetch");
var secondWait = LyricsLookup.Step(automatic.State, new LyricsInput.Completed(automatic.State.Attempt, new LyricsResult.Missing()));
Check(secondWait.Command is ScheduleLyricsRetry {Delay.TotalSeconds:8}, "second automatic backoff");
var exhaustedState = secondWait.State;
for (int n = 0; n < 2; n++) {
    var due = LyricsLookup.Step(exhaustedState, new LyricsInput.RetryDue(exhaustedState.Attempt));
    exhaustedState = LyricsLookup.Step(due.State, new LyricsInput.Completed(due.State.Attempt, new LyricsResult.Missing())).State;
}
var lastRequest = LyricsLookup.Step(exhaustedState, new LyricsInput.RetryDue(exhaustedState.Attempt));
var exhausted = LyricsLookup.Step(lastRequest.State, new LyricsInput.Completed(lastRequest.State.Attempt, new LyricsResult.Missing()));
Check(exhausted.State is LyricsState.Finished && exhausted.Command is null, "missing lyrics stop retrying after four retries");
Check(LyricsLookup.RetryDelay(new LyricsResult.Failed("offline"), 50) == TimeSpan.FromSeconds(60), "transient failure keeps retrying with capped backoff");
Check(LyricsMatching.Select([new LrclibResponse { TrackName = "Song", ArtistName = "Artist", Duration = 190, SyncedLyrics = "[00:01.00]ok" }], query) is not null,
    "tolerate ten-second duration drift");
Check(LyricsMatching.Select([new LrclibResponse { TrackName = "Song", ArtistName = "Artist - Topic", Duration = 180, SyncedLyrics = "[00:01.00]ok" }], query) is not null,
    "artist Topic suffix ignored");
Check(LyricsMatching.Select([new LrclibResponse { TrackName = "Song", ArtistName = "Someone Else", Duration = 181, SyncedLyrics = "[00:01.00]ok" }], query) is not null,
    "identical title and near-identical length matches despite artist spelling");
Check(LyricsMatching.CleanTitle("Song - Remastered 2011") == "Song", "remaster suffix removed");
Check(LyricsLookup.Step(firstRequest.State, new LyricsInput.Completed(firstRequest.State.Attempt,
    new LyricsResult.Failed("permanent", Transient:false))).Command is null, "don't retry permanent failure");
var wrongVersion = new LrclibResponse { TrackName = "Song", ArtistName = "Artist", Duration = 240, SyncedLyrics = "[00:01.00]wrong version" };
Check(LyricsMatching.Select([wrongVersion], query) is null, "reject mismatched duration");
Check(LyricsMatching.Decode(new LrclibResponse {SyncedLyrics="[00:00.00] "}) is LyricsResult.Missing, "blank LRC isn't synced lyrics");
Check(LrcParser.IndexAt(LrcParser.Parse("[00:10.00]first\n[00:20.00]second"), TimeSpan.FromSeconds(15)) == 0, "timeline selects current line");
Check(LrcParser.Parse("[00:NaN]bad\n[00:Infinity]bad\n[00:60]bad\n[00:02.00]good").Count == 1,
    "invalid provider timestamps must be rejected without throwing");

// Actual Apple Music Windows regression: artist and album are merged in SMTC metadata.
const string apple = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
var corrected = MediaMetadata.ArtistAlbum(apple, "Dongker \u2014 Ceriwis Necis", "");
Check(corrected == ("Dongker", "Ceriwis Necis"), "Apple Music artist/album split");
Check(MediaMetadata.ArtistAlbum(apple, "Artist \u2014 Album \u2014 Radio", "Explicit album") == ("Artist", "Explicit album"), "preserve explicit album");
Check(MediaMetadata.ArtistAlbum("spotify.exe", "Artist \u2014 Name", "Album").Artist == "Artist \u2014 Name", "don't split other providers");
Check(MediaMetadata.ProviderName(apple) == "Apple Music", "provider display name, not package id");
Check(MediaMetadata.ProviderName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "Spotify", "Spotify provider");
Check(LyricsMatching.CleanTitle("Luka di Pelupuk Mata (feat. Binar)") == "Luka di Pelupuk Mata", "featured title normalization");
Check(LyricsMatching.CleanTitle("Luka di Pelupuk Mata ft. Binar") == "Luka di Pelupuk Mata", "unbracketed featured title");
Check(LyricsMatching.CleanTitle("Song (Live)") == "Song (Live)", "preserve recording version");
var actualQuery = new LyricsQuery(corrected.Artist, "Luka di Pelupuk Mata (feat. Binar)", TimeSpan.FromSeconds(291)) {Album=corrected.Album};
var actualCandidates = new[] {
    new LrclibResponse {TrackName="Luka di Pelupuk Mata (feat. Binar)", ArtistName="Dongker", AlbumName="Ceriwis Necis", Duration=5, SyncedLyrics="[00:01.00]stub"},
    new LrclibResponse {TrackName="Luka di Pelupuk Mata", ArtistName="Dongker", AlbumName="Ceriwis Necis", Duration=292, PlainLyrics="plain"},
    new LrclibResponse {TrackName="Luka di Pelupuk Mata", ArtistName="Dongker, Binar", AlbumName="Ceriwis Necis", Duration=292, SyncedLyrics="[00:10.00]correct version"}
};
Check(LyricsMatching.Select(actualCandidates, actualQuery) == actualCandidates[2], "select synced collaboration over plain record, reject five-second stub");
Check(LyricsMatching.Decode(actualCandidates[0], actualQuery.Duration) is LyricsResult.Plain, "out-of-duration exact result must not pretend to sync");
// Exhaustively explore short event sequences, checking stale, duplicate, retry and reset invariants.
LyricsInput[] alphabet = [new LyricsInput.Request(query), new LyricsInput.Request(query with {Title="Other"}),
    new LyricsInput.RetryDue(1), new LyricsInput.RetryDue(2), new LyricsInput.Reset(), new LyricsInput.Completed(1, new LyricsResult.Missing()),
    new LyricsInput.Completed(2, new LyricsResult.Plain("old")), new LyricsInput.Completed(3, new LyricsResult.Instrumental())];
int paths = 0;
void Explore(LyricsState state, List<LyricsInput> history, int depth) {
    Check(LyricsLookup.Run(new LyricsState.Idle(), history) == state, "replay equals stepped state");
    if (depth == 0) {paths++; return;}
    foreach(var input in alphabet) {
        var (next, command) = LyricsLookup.Step(state, input);
        if (input is LyricsInput.Completed completed && (state is not LyricsState.Loading || state.Attempt != completed.Attempt))
            Check(ReferenceEquals(state, next) && command is null, "stale or duplicate completion changed current request");
        if (input is LyricsInput.Request) Check(next.Attempt == state.Attempt + 1 && command?.Attempt == next.Attempt, "retry has a fresh generation");
        if (input is LyricsInput.Reset) Check(next is LyricsState.Idle && next.Attempt > state.Attempt, "reset fences old requests");
        history.Add(input); Explore(next, history, depth - 1); history.RemoveAt(history.Count-1);
    }
}
Explore(new LyricsState.Idle(), [], 5);
Console.WriteLine($"PASS: lyrics fallback/cache/errors/cancellation/matching and {paths} lifecycle event sequences");

// Artwork regressions: late provider updates, A -> B -> A, null thumbnails and overlapping loads.
byte[] oldCover = [1], newCover = [2];
ArtworkState art = new ArtworkState.Idle();
var artA = ArtworkLookup.Step(art, new ArtworkInput.Request("provider/album/trackA", false));
art = ArtworkLookup.Step(artA.State, new ArtworkInput.Completed(artA.Command!.Attempt, oldCover)).State;
var artB = ArtworkLookup.Step(art, new ArtworkInput.Request("provider/album/trackB", false));
Check(artB.State.Bytes is null, "new song must not inherit the previous cover");
// Simulate Windows initially handing back the old cover under the new title.
art = ArtworkLookup.Step(artB.State, new ArtworkInput.Completed(artB.Command!.Attempt, oldCover)).State;
var lateArtwork = ArtworkLookup.Step(art, new ArtworkInput.Request("provider/album/trackB", true));
Check(lateArtwork.Command is not null, "metadata artwork update must bypass same-title cache");
art = ArtworkLookup.Step(lateArtwork.State, new ArtworkInput.Completed(lateArtwork.Command!.Attempt, newCover)).State;
Check(ReferenceEquals(art.Bytes, newCover), "late cover replaces old cover");
var playbackOnly = ArtworkLookup.Step(art, new ArtworkInput.Request("provider/album/trackB", false));
Check(playbackOnly.Command is null && ReferenceEquals(playbackOnly.State.Bytes, newCover), "play/pause can use a valid cover cache");
var backToA = ArtworkLookup.Step(art, new ArtworkInput.Request("provider/album/trackA", false));
Check(ReferenceEquals(ArtworkLookup.Step(backToA.State, new ArtworkInput.Completed(artA.Command.Attempt, oldCover)).State, backToA.State),
    "old A load must not complete the later A request");
var missingArt = ArtworkLookup.Step(backToA.State, new ArtworkInput.Completed(backToA.Command!.Attempt, null)).State;
Check(missingArt is ArtworkState.Missing && missingArt.Bytes is null, "missing cover clears stale data");
Check(ArtworkLookup.Step(missingArt, new ArtworkInput.Request("provider/album/trackA", false)).Command is not null,
    "missing cover is retryable");
ArtworkInput[] artInputs = [new ArtworkInput.Request("A", false), new ArtworkInput.Request("B", true),
    new ArtworkInput.Reset(), new ArtworkInput.Completed(1, oldCover),
    new ArtworkInput.Completed(2, newCover), new ArtworkInput.Completed(3, null)];
int artPaths = 0;
void ExploreArtwork(ArtworkState state, List<ArtworkInput> history, int depth)
{
    Check(ArtworkLookup.Run(new ArtworkState.Idle(), history) == state, "artwork replay");
    if (depth == 0) {artPaths++; return;}
    foreach (var input in artInputs)
    {
        var (next, command) = ArtworkLookup.Step(state, input);
        if (input is ArtworkInput.Completed completed && (state is not ArtworkState.Loading || completed.Attempt != state.Attempt))
            Check(ReferenceEquals(next, state) && command is null, "stale artwork completion changed state");
        if (input is ArtworkInput.Request request)
        {
            if (request.Key != state.Key) Check(next.Bytes is null, "cover leaked across track identities");
            if (request.Refresh) Check(command is not null && command.Attempt > state.Attempt, "forced artwork update reused stale cache");
        }
        if (input is ArtworkInput.Reset) Check(next is ArtworkState.Idle && next.Bytes is null, "session reset retained artwork");
        history.Add(input); ExploreArtwork(next, history, depth - 1); history.RemoveAt(history.Count - 1);
    }
}
ExploreArtwork(new ArtworkState.Idle(), [], 5);
Console.WriteLine($"PASS: artwork late updates/cache/null/stale loads and {artPaths} lifecycle event sequences");
Check(MusicGlance.Resolve(true, "current lyric", "next lyric") == new MusicGlance("current lyric", true), "closed notch shows the current synced lyric");
Check(MusicGlance.Resolve(true, "\u266a", "first lyric") == new MusicGlance("first lyric", true), "intro previews lyric instead of a music-note placeholder");
Check(MusicGlance.Resolve(false, "stale lyric", "stale next") == new MusicGlance("", false), "unsynced or unavailable lyrics show no text, never title/artist");
Check(MusicGlance.Resolve(true, " ", "") == new MusicGlance("", false), "blank synced lines show no text");var idleFocus=FocusTimer.Initial(FocusConfig.Default);
Check(FocusTimer.Step(idleFocus,new FocusInput.Skip(),FocusConfig.Default).State==idleFocus,"idle Skip must not change phase or round");
var manualShort=FocusTimer.Step(idleFocus,new FocusInput.Select(FocusMode.ShortBreak),FocusConfig.Default).State;
for(int segment=1;segment<=4;segment++) Check(FocusTimer.Segment(manualShort,FocusConfig.Default,segment)==0,"choosing a break must not mark a focus session completed");
manualShort=FocusTimer.Step(manualShort,new FocusInput.Toggle(),FocusConfig.Default).State;
manualShort=FocusTimer.Step(manualShort,new FocusInput.Tick(TimeSpan.FromMinutes(1)),FocusConfig.Default).State;
Check(FocusTimer.Segment(manualShort,FocusConfig.Default,1)==0,"elapsed break time must not fill focus bars");
var partial=FocusTimer.Step(idleFocus,new FocusInput.Toggle(),FocusConfig.Default).State;
partial=FocusTimer.Step(partial,new FocusInput.Tick(TimeSpan.FromMinutes(10)),FocusConfig.Default).State;
Check(Math.Abs(FocusTimer.Segment(partial,FocusConfig.Default,1)-0.4)<0.0001,"active Focus bar shows elapsed fraction");
var skipped=FocusTimer.Step(partial,new FocusInput.Skip(),FocusConfig.Default).State;
Check(skipped.Completed==0&&FocusTimer.Segment(skipped,FocusConfig.Default,1)==0,"skipped focus must not earn a full bar");
var earned=FocusTimer.Step(partial,new FocusInput.Tick(TimeSpan.FromMinutes(15)),FocusConfig.Default).State;
Check(earned.Mode==FocusMode.ShortBreak&&FocusTimer.Segment(earned,FocusConfig.Default,1)==1,"completed Focus keeps its bar full during break");
var paused=FocusTimer.Step(partial,new FocusInput.Toggle(),FocusConfig.Default).State;
Check(FocusTimer.Segment(paused,FocusConfig.Default,1)==0.4,"paused Focus keeps partial progress");
var reset=FocusTimer.Step(paused,new FocusInput.Reset(),FocusConfig.Default).State;
Check(FocusTimer.Segment(reset,FocusConfig.Default,1)==0,"reset clears unfinished progress");
var focusConfig=new FocusConfig(TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(6));
var focus=FocusTimer.Initial(focusConfig);
focus=FocusTimer.Step(focus,new FocusInput.Toggle(),focusConfig).State;
focus=FocusTimer.Step(focus,new FocusInput.Tick(TimeSpan.FromSeconds(4)),focusConfig).State;
focus=FocusTimer.Step(focus,new FocusInput.Toggle(),focusConfig).State;
Check(focus.Run==FocusRun.Paused && focus.Remaining==TimeSpan.FromSeconds(6),"pause preserves remaining time");
Check(FocusTimer.Step(focus,new FocusInput.Tick(TimeSpan.FromHours(1)),focusConfig).State==focus,"paused timer does not advance");
focus=FocusTimer.Step(focus,new FocusInput.Toggle(),focusConfig).State;
focus=FocusTimer.Step(focus,new FocusInput.Tick(TimeSpan.FromSeconds(7)),focusConfig).State;
Check(focus.Mode==FocusMode.ShortBreak && focus.Completed==1 && focus.Remaining==TimeSpan.FromSeconds(2),"phase transition carries elapsed overshoot");
focus=FocusTimer.Step(focus,new FocusInput.Tick(TimeSpan.FromSeconds(2)),focusConfig).State;
Check(focus.Mode==FocusMode.Focus && focus.Round==2 && focus.Run==FocusRun.Idle,"completed break readies next focus session");
for(int round=2;round<=4;round++) {
    focus=FocusTimer.Step(focus,new FocusInput.Toggle(),focusConfig).State;
    focus=FocusTimer.Step(focus,new FocusInput.Tick(focusConfig.Focus),focusConfig).State;
    Check(focus.Mode==(round==4?FocusMode.LongBreak:FocusMode.ShortBreak),"fourth session earns long break");
    focus=FocusTimer.Step(focus,new FocusInput.Tick(focus.Remaining),focusConfig).State;
}
Check(focus.Round==1 && focus.Completed==4,"long break resets round and retains completed count");
focus=FocusTimer.Step(focus,new FocusInput.Skip(),focusConfig).State;
Check(focus.Completed==4,"skip does not count as completed focus");
focus=FocusTimer.Step(focus,new FocusInput.Reset(),focusConfig).State;
Check(focus.Run==FocusRun.Idle && focus.Remaining==focusConfig.Duration(focus.Mode),"reset restores selected phase duration");
var extreme=FocusTimer.Step(FocusTimer.Initial(focusConfig) with {Run=FocusRun.Running},new FocusInput.Tick(TimeSpan.FromDays(1)),focusConfig);
Check(extreme.State.Run==FocusRun.Idle && extreme.State.Completed==1,"long sleep cannot fabricate completed focus sessions");
FocusInput[] focusInputs=[new FocusInput.Toggle(),new FocusInput.Reset(),new FocusInput.Skip(),new FocusInput.Select(FocusMode.LongBreak),new FocusInput.Tick(TimeSpan.FromSeconds(7))];
int focusPaths=0;
void ExploreFocus(FocusTimerState state,int depth){
    Check(state.Remaining>TimeSpan.Zero && state.Remaining<=focusConfig.Duration(state.Mode),"remaining time invariant");
    Check(state.Round>=1&&state.Round<=4,"round bounds");
    for(int segment=1;segment<=4;segment++){var value=FocusTimer.Segment(state,focusConfig,segment);Check(value>=0&&value<=1,"focus segment bounds" );if(state.Mode!=FocusMode.Focus && (state.CompletedMask & (1<<(segment-1)))==0)Check(value==0,"break cannot invent completed sessions" );}
    if(depth==0){focusPaths++;return;}
    foreach(var input in focusInputs){var next=FocusTimer.Step(state,input,focusConfig).State;Check(next.Completed>=state.Completed,"completed sessions never decrease");ExploreFocus(next,depth-1);}
}
ExploreFocus(FocusTimer.Initial(focusConfig),6);
Console.WriteLine($"PASS: focus pause/resume/round/reset/skip/sleep and {focusPaths} event sequences");
var prayerDay = new PrayerTimes { Fajr = new(4,30), Sunrise = new(5,45), Dhuhr = new(12,0), Asr = new(15,0), Maghrib = new(18,0), Isha = new(19,0) };
var prayerDate = new DateTime(2026,11,4);
Check(prayerDay.GetNext(prayerDate.AddHours(5)).Name == "Dhuhr", "sunrise is not a prayer candidate");
Check(prayerDay.GetNext(prayerDate.AddHours(12)).Name == "Asr", "prayer boundary advances to next prayer");
Check(prayerDay.GetNext(prayerDate.AddHours(20)).At == prayerDate.AddDays(1).AddHours(4.5), "prayer rollover");
Check(PrayerPresentation.Countdown(prayerDate,prayerDate.AddSeconds(3661)) == "01:01:01", "prayer countdown hours");
Check(PrayerPresentation.Countdown(prayerDate,null) == "--:--:--", "missing schedule countdown");
Check(PrayerPresentation.Countdown(prayerDate,prayerDate.AddSeconds(-1)) == "00:00:00", "expired countdown clamps");
double previousProgress = 0;
for(int minute=0; minute<1440; minute++) {
    var value=PrayerPresentation.Progress(prayerDay,prayerDate.AddMinutes(minute));
    Check(value>=previousProgress && value>=0 && value<=1,"prayer arc monotonic bounds");previousProgress=value;
    var point=PrayerPresentation.ArcPoint(value);Check(point.X>=30&&point.X<=270&&point.Y>=0&&point.Y<=64,"prayer marker follows arc");
}
var instant=new DateTimeOffset(2026,11,4,17,0,0,TimeSpan.Zero);
Check(PrayerLocation.Places[0].LocalNow(instant).Date==prayerDate.AddDays(1),"location midnight follows city timezone");
Check(PrayerLocation.Places[^1].LocalNow(instant).Hour==2,"WIT offset");
foreach(var place in PrayerLocation.Places) Check(place.ToMachineTime(place.LocalNow(instant)).ToUniversalTime()==instant.UtcDateTime,"city time roundtrip");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(5).AddTicks(-1))==PrayerDayPeriod.Night,"night ends before 05:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(5))==PrayerDayPeriod.Morning,"morning starts at 05:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(11).AddTicks(-1))==PrayerDayPeriod.Morning,"morning ends before 11:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(11))==PrayerDayPeriod.Midday,"midday starts at 11:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(15).AddTicks(-1))==PrayerDayPeriod.Midday,"midday ends before 15:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(15))==PrayerDayPeriod.Afternoon,"afternoon starts at 15:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(18).AddTicks(-1))==PrayerDayPeriod.Afternoon,"afternoon ends before 18:00");
Check(PrayerPresentation.DayPeriod(prayerDate.AddHours(18))==PrayerDayPeriod.Night,"night starts at 18:00");
int[] periodMinutes=new int[4];
for(int minute=0;minute<1440;minute++) periodMinutes[(int)PrayerPresentation.DayPeriod(prayerDate.AddMinutes(minute))]++;
Check(periodMinutes.SequenceEqual(new[]{360,240,180,660}),"all 24 hours partitioned into four periods");
var periodInstant = new DateTimeOffset(2026,11,4,21,0,0,TimeSpan.Zero);
Check(PrayerPresentation.DayPeriod(PrayerLocation.Places[0].LocalNow(periodInstant))==PrayerDayPeriod.Night,"WIB 04:00 is night");
Check(PrayerPresentation.DayPeriod(PrayerLocation.Places[^1].LocalNow(periodInstant))==PrayerDayPeriod.Morning,"same instant WIT 06:00 is morning");
var bogorCity=PrayerLocation.Places.Single(p=>p.Id=="bogor-city");
var bogorRegency=PrayerLocation.Places.Single(p=>p.Id=="bogor-regency");
Check(bogorCity.DisplayCity=="Kota Bogor" && bogorRegency.DisplayCity=="Kabupaten Bogor","Bogor administrative labels");
Check(bogorCity.Latitude!=bogorRegency.Latitude,"Bogor city and regency have separate coordinates");
const string prayerJson = """{"data":{"timings":{"Imsak":"04:20 (WIB)","Fajr":"04:30","Sunrise":"05:45","Dhuhr":"12:00","Asr":"15:00","Maghrib":"18:00","Isha":"19:00"}}}""";
Check(PrayerTimes.ParseDay(prayerJson)?.Imsak == new TimeOnly(4,20),"Imsak parsed from API with timezone suffix");
Check(PrayerTimes.ParseDay(prayerJson.Replace("04:20 (WIB)","invalid")) is {Imsak:null},"invalid optional Imsak does not break prayers");
Check(PrayerTimes.ParseDay(prayerJson.Replace("\"Imsak\":\"04:20 (WIB)\",","")) is {Imsak:null},"missing Imsak remains unavailable");
Check(PrayerTimes.ParseDay(prayerJson)!.GetNext(prayerDate.AddHours(4)).Name == "Fajr","Imsak is informational, not a prayer reminder");
Check(Math.Abs(PrayerLocation.Places[0].QiblaBearing-295.15)<0.1,"Jakarta Qibla matches reference bearing");
foreach(var place in PrayerLocation.Places) Check(double.IsFinite(place.QiblaBearing) && place.QiblaBearing>=0 && place.QiblaBearing<360,"Qibla bearing normalized");
Console.WriteLine("PASS: Imsak parsing/missing/invalid and Qibla bearings");
Console.WriteLine("PASS: prayer boundaries, countdown, 1440 timeline points, four day periods and city timezones");
var usageNow = new DateTimeOffset(2026,11,4,12,0,0,TimeSpan.Zero);
const string codexUsageJson = """{"rateLimits":{"planType":"plus","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1793811018},"secondary":{"usedPercent":80,"windowDurationMins":10080}}}""";
var codexUsage = AgentUsageParser.Parse(UsageProvider.Codex,codexUsageJson,usageNow);
Check(codexUsage.Snapshot is {Plan:"plus",Windows.Count:2} && codexUsage.Snapshot.Windows[0].UsedPercent==25,"Codex app-server windows parsed");
Check(AgentUsageParser.Parse(UsageProvider.Codex,codexUsageJson.Replace("25","125"),usageNow).Snapshot!.Windows.Count==1,"out-of-range percent rejected, not clamped");
Check(AgentUsageParser.Parse(UsageProvider.Codex,"{}",usageNow).Snapshot is null,"missing quotas never shown as zero");
Check(AgentUsageParser.Parse(UsageProvider.Codex,"not json",usageNow).Problem==UsageProblem.InvalidData,"malformed usage body rejected");
const string claudeUsageJson = """{"five_hour":{"utilization":12.5,"resets_at":"2026-11-05T00:00:00Z"},"seven_day":null,"limits":[{"kind":"weekly_all","percent":90},{"kind":"session","percent":30}]}""";
var claudeUsage=AgentUsageParser.Parse(UsageProvider.Claude,claudeUsageJson,usageNow);
Check(claudeUsage.Snapshot is {Windows.Count:2} && claudeUsage.Snapshot.Windows[0].UsedPercent==12.5,"Claude flat and newer limits format without duplicates");
const string agyUsageJson = """{"status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[{"name":"Gemini","buckets":[{"window":"weekly","remaining_fraction":0.25},{"window":"5h","remaining_fraction":0,"disabled":true}]}]}}}""";
var agyUsage=AgentUsageParser.Parse(UsageProvider.Antigravity,agyUsageJson,usageNow);
Check(agyUsage.Snapshot is {Windows.Count:1} && agyUsage.Snapshot.Windows[0].UsedPercent==75,"remaining fraction becomes used percent and disabled pools excluded");
Check(AgentUsageParser.Parse(UsageProvider.Antigravity,agyUsageJson.Replace("\"num_turns\":0","\"num_turns\":1"),usageNow).Problem==UsageProblem.InvalidData,"model turns cannot be accepted as usage results");
const string museUsageJson = """{"schemaVersion":1,"allowance":{"tier":"pro","window":{"usedPercent":40,"resetsAtMs":1793811018000},"weekly":{"usedPercent":70},"asOfMs":1793793600000}}""";
var museUsage=AgentUsageParser.Parse(UsageProvider.Muse,museUsageJson,usageNow);
Check(museUsage.Snapshot is {Windows.Count:2} && museUsage.Snapshot.Windows[0].ResetsAt==DateTimeOffset.FromUnixTimeMilliseconds(1793811018000),"Muse milliseconds and allowance windows parsed");
Check(AgentUsageParser.Parse(UsageProvider.Muse,"""{"allowance":null,"reason":"server_sent_no_usage"}""",usageNow).Problem==UsageProblem.NoData,"Muse absent allowance is an explicit no-data state");
var usageLoad=UsageLoad.Initial.Begin();int firstUsageAttempt=usageLoad.Generation;
usageLoad=usageLoad.Complete(firstUsageAttempt,codexUsage).Begin();
Check(usageLoad.Complete(firstUsageAttempt,new(null,UsageProblem.Offline))==usageLoad,"late failed usage result cannot overwrite newer load");
usageLoad=usageLoad.Complete(usageLoad.Generation,new(null,UsageProblem.Offline));
Check(usageLoad.Content is UsageContent.Failed {Previous:not null},"refresh error retains a labelled previous reading");
int usagePaths=0;
void ExploreUsage(UsageLoad load,int depth)
{
    if(depth==0){usagePaths++;return;}
    foreach(var next in new[]{load.Begin(),load.Complete(load.Generation,codexUsage),load.Complete(load.Generation,new(null,UsageProblem.Offline)),load.Complete(load.Generation-1,codexUsage)})
    {
        Check(next.Generation>=load.Generation,"usage generation never decreases");
        if(load.Content is not UsageContent.Loading) Check(load.Complete(load.Generation,codexUsage)==load,"duplicate completion ignored");
        ExploreUsage(next,depth-1);
    }
}
ExploreUsage(UsageLoad.Initial,6);
var usageClock=usageNow;
var usageFake=new FakeUsageReader();
using(var usageService=new AgentUsageService(usageFake,()=>usageClock))
{
    var firstFetch=usageService.RefreshAsync();
    await usageService.RefreshAsync();
    Check(usageFake.Calls.Values.Sum()==4,"concurrent refresh is single-flight per provider");
    foreach(var provider in Enum.GetValues<UsageProvider>()) usageFake.Complete(provider,codexUsage with {Snapshot=codexUsage.Snapshot! with {Provider=provider}});
    await firstFetch;
    await usageService.RefreshAsync();
    Check(usageFake.Calls.Values.Sum()==4,"cooldown blocks repeated quota probes");
    usageClock+=TimeSpan.FromMinutes(3);
    usageService.SetEnabled(UsageProvider.Claude,false);
    var nextFetch=usageService.RefreshAsync();
    Check(usageFake.Calls[UsageProvider.Codex]==2 && usageFake.Calls[UsageProvider.Claude]==1 && usageFake.Calls[UsageProvider.Antigravity]==1 && usageFake.Calls[UsageProvider.Muse]==1,"enabled state and per-provider cooldown respected");
    usageFake.Complete(UsageProvider.Codex,new(null,UsageProblem.RateLimited,usageClock.AddHours(1)));
    await nextFetch;
    usageClock+=TimeSpan.FromMinutes(3);
    var afterRateLimit=usageService.RefreshAsync();
    usageFake.Complete(UsageProvider.Muse,museUsage);
    await afterRateLimit;
    Check(usageFake.Calls[UsageProvider.Codex]==2,"Retry-After prevents early probes");
}
var cancellingFake=new FakeUsageReader();
var cancellingService=new AgentUsageService(cancellingFake,()=>usageNow);
var cancelledRefresh=cancellingService.RefreshAsync();cancellingService.Dispose();await cancelledRefresh;
Check(cancellingFake.Cancelled==4,"shutdown cancels all pending providers");
using(var throwingService=new AgentUsageService(new ThrowingUsageReader(),()=>usageNow))
{
    await throwingService.RefreshAsync();
    Check(Enum.GetValues<UsageProvider>().All(p=>throwingService.GetState(p).Content is UsageContent.Failed),"an unexpected reader exception fails the load instead of leaving it Loading forever");
}
{
    string stampDir=Path.Combine(Path.GetTempPath(),"mynotch-agy-"+Guid.NewGuid());Directory.CreateDirectory(stampDir);
    try
    {
        string stamp=Path.Combine(stampDir,"last_check.timestamp");var t0=new DateTime(2026,11,6,12,0,0,DateTimeKind.Utc);
        AgentUsageReader.DeferAgyUpdateCheck(stamp,t0);
        Check(!File.Exists(stamp),"agy stamp never created when agy has none");
        File.WriteAllBytes(stamp,[]);File.SetLastWriteTimeUtc(stamp,t0.AddHours(-3));
        AgentUsageReader.DeferAgyUpdateCheck(stamp,t0);
        Check(File.GetLastWriteTimeUtc(stamp)==t0.AddMinutes(-14) && new FileInfo(stamp).Length==0,"stale agy stamp backdated to 14 minutes, content untouched");
        File.SetLastWriteTimeUtc(stamp,t0.AddMinutes(-2));
        AgentUsageReader.DeferAgyUpdateCheck(stamp,t0);
        Check(File.GetLastWriteTimeUtc(stamp)==t0.AddMinutes(-2),"a recent agy check is left alone");
    }
    finally{try{Directory.Delete(stampDir,true);}catch(IOException){}}
}
Console.WriteLine($"PASS: four usage schemas, no-data/errors, cooldown/single-flight/cancellation and {usagePaths} lifecycle sequences");
var leapCalendar = CalendarMonth.Cells(new DateOnly(2024,2,1));
Check(leapCalendar.Count==42 && leapCalendar[0]!.Value.DayOfWeek==DayOfWeek.Monday,"calendar has six Monday-first weeks");
Check(leapCalendar.Count(d=>d is {Year:2024,Month:2})==29,"leap February has 29 days");
Check(CalendarMonth.Move(new DateOnly(2026,12,1),1)==new DateOnly(2027,1,1),"calendar year rollover");
Check(CalendarMonth.Move(DateOnly.MinValue,-1)==DateOnly.MinValue,"calendar lower bound");
Check(CalendarMonth.Move(new DateOnly(9999,12,1),1)==new DateOnly(9999,12,1),"calendar upper bound");
foreach(int year in new[]{1,1900,2000,2026,9999}) foreach(int month in Enumerable.Range(1,12))
{
    var cells=CalendarMonth.Cells(new DateOnly(year,month,1));
    Check(cells.Count(d=>d is {} date && date.Year==year && date.Month==month)==DateTime.DaysInMonth(year,month),"every day of the month appears once");
    var valid=cells.Where(d=>d is not null).Select(d=>d!.Value.DayNumber).ToArray();
    Check(valid.Distinct().Count()==valid.Length && valid.Zip(valid.Skip(1),(a,b)=>b-a).All(d=>d==1),"calendar dates are consecutive and unique");
}
Check(SystemUsageMath.CpuPercent(null,new(100,200,100)) is null,"CPU requires two samples");
Check(SystemUsageMath.CpuPercent(new(100,200,100),new(150,300,200))==75,"kernel includes idle in CPU calculation");
Check(SystemUsageMath.CpuPercent(new(1,2,3),new(1,2,3)) is null,"zero elapsed CPU sample unavailable");
Check(SystemUsageMath.CpuPercent(new(100,200,100),new(90,201,101)) is null,"CPU counter reset rejected");
var oldNetwork=new Dictionary<string,NetworkCounters>{{"wifi",new(1000,2000)}};
var newNetwork=new Dictionary<string,NetworkCounters>{{"wifi",new(3000,3000)},{"ethernet",new(90000000,90000000)}};
Check(SystemUsageMath.NetworkRate(oldNetwork,newNetwork,2)==(1000d,500d),"network rates ignore new adapter lifetime totals");
Check(SystemUsageMath.NetworkRate(newNetwork,oldNetwork,2)==(0d,0d),"network counter reset cannot produce negative speed");
Check(SystemUsageMath.NetworkRate(null,newNetwork,2)==(null,null),"first network sample is unavailable");
Check(SystemUsageMath.Bytes(1536)=="1.5 KB" && SystemUsageMath.Bytes(null)=="--","system usage unit formatting");
Console.WriteLine("PASS: calendar month/leap-year/bounds and CPU/network sampling math");
var todoDate = new DateOnly(2026,11,4);
var todoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
var todoId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
IReadOnlyList<CalendarTask> todoItems=Array.Empty<CalendarTask>();
Check(CalendarTodo.Step(todoItems,new TodoInput.Add(todoId,todoDate,"  ")).Problem==TodoProblem.Empty,"blank tasks rejected");
Check(CalendarTodo.Step(todoItems,new TodoInput.Add(todoId,todoDate,new string('a',201))).Problem==TodoProblem.TooLong,"task text bounds");
todoItems=CalendarTodo.Step(todoItems,new TodoInput.Add(todoId,todoDate,"  Plan next release  ")).Items;
Check(todoItems[0].Text=="Plan next release" && todoItems[0].Date==todoDate,"task trimmed and assigned to selected date");
Check(CalendarTodo.Step(todoItems,new TodoInput.Add(todoId,todoDate,"duplicate")).Problem==TodoProblem.Duplicate,"duplicate task IDs rejected");
Check(CalendarTodo.Step(todoItems,new TodoInput.Complete(todoId,true)).Items.Single().Completed,"task completion");
var todoInputs=new TodoInput[]{new TodoInput.Add(todoId,todoDate,"First"),new TodoInput.Add(todoId2,todoDate.AddDays(1),"Second"),new TodoInput.Complete(todoId,true),new TodoInput.Complete(todoId,false),new TodoInput.Remove(todoId)};
int todoPaths=0;
void ExploreTodos(IReadOnlyList<CalendarTask> items,int depth)
{
    Check(CalendarTodo.Valid(items),"task collection remains valid");
    if(depth==0){todoPaths++;return;}
    foreach(var input in todoInputs)ExploreTodos(CalendarTodo.Step(items,input).Items,depth-1);
}
ExploreTodos(Array.Empty<CalendarTask>(),5);
string todoTestDirectory=Path.Combine(Path.GetTempPath(),"my-notch-todo-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(todoTestDirectory);
string todoTestFile=Path.Combine(todoTestDirectory,"tasks.json");
string todoBlockedParent=Path.Combine(todoTestDirectory,"blocked-parent");
try
{
    var store=new CalendarTodoService(todoTestFile);
    Check(store.Apply(new TodoInput.Add(todoId,todoDate,"Persist task")),"task persisted");
    store=new CalendarTodoService(todoTestFile);
    Check(store.Items.Count==1 && store.Items[0].Date==todoDate,"task survives reload");
    Check(store.Apply(new TodoInput.Complete(todoId,true)),"completion persisted");
    Check(new CalendarTodoService(todoTestFile).Items.Single().Completed,"completion survives reload");
    Check(store.Apply(new TodoInput.Remove(todoId)) && new CalendarTodoService(todoTestFile).Items.Count==0,"deletion persists");
    File.WriteAllText(todoTestFile,"invalid json");
    var corrupt=new CalendarTodoService(todoTestFile);
    Check(!corrupt.Apply(new TodoInput.Add(todoId,todoDate,"Do not overwrite")) && File.ReadAllText(todoTestFile)=="invalid json","corrupt file preserved");
    File.WriteAllText(todoBlockedParent,"keep");
    var blocked=new CalendarTodoService(Path.Combine(todoBlockedParent,"tasks.json"));
    Check(!blocked.Apply(new TodoInput.Add(todoId,todoDate,"Write fails")) && blocked.Items.Count==0 && File.ReadAllText(todoBlockedParent)=="keep","failed save does not claim task was persisted");
}
finally
{
    foreach(var file in Directory.GetFiles(todoTestDirectory))File.Delete(file);
    Directory.Delete(todoTestDirectory);
}
Console.WriteLine($"PASS: dated to-do validation/complete/delete, atomic persistence/error handling and {todoPaths} event sequences");
var creditJson="""{"rateLimits":{"primary":{"usedPercent":100,"windowDurationMins":300}},"rateLimitResetCredits":{"availableCount":2,"credits":[{"id":"credit-1","status":"available","title":"Full reset","expiresAt":1999999999},{"id":"expired","status":"available","expiresAt":1},{"id":"used","status":"consumed"}]}}""";
var creditUsage=AgentUsageParser.Parse(UsageProvider.Codex,creditJson,usageNow).Snapshot!;
Check(creditUsage.AvailableResets==2 && creditUsage.ResetCredits is {Count:1} && creditUsage.ResetCredits[0].Id=="credit-1","reset parser accepts only available unexpired credits");
Check(UsageReset.Key("credit-1")==UsageReset.Key("credit-1") && UsageReset.Key("credit-1")!=UsageReset.Key("credit-2") && Guid.TryParse(UsageReset.Key("credit-1"),out _),"stable per-credit reset idempotency survives retries and restart");
var resetFake=new FakeUsageResetReader(creditUsage);
using(var resetService=new AgentUsageService(resetFake,()=>usageNow))
{
    await resetService.RefreshAsync();
    var resetTask=resetService.ResetCodexAsync();
    await resetService.ResetCodexAsync();
    Check(resetFake.ResetCalls==1,"reset is single-flight");
    resetFake.Completion.SetResult(UsageResetOutcome.Reset);
    await resetTask;
    Check(resetFake.ReadCalls==5 && resetService.GetState(UsageProvider.Codex).Snapshot?.AvailableResets==0,"reset refreshes real quota data despite normal cooldown");
    await resetService.ResetCodexAsync();
    Check(resetFake.ResetCalls==1,"no available credit cannot issue another reset");
}
Console.WriteLine("PASS: reset-credit eligibility, stable idempotency, duplicate-click guard and post-reset refresh (fake provider only)");
string resetDirectory=Path.Combine(Path.GetTempPath(),"my-notch-reset-test-"+Guid.NewGuid().ToString("N"));
try
{
    var journal=new UsageResetJournal(resetDirectory);
    string attempt=journal.GetOrCreate("test-credit");
    journal.Record("test-credit",UsageResetOutcome.Unknown);
    Check(new UsageResetJournal(resetDirectory).GetOrCreate("test-credit")==attempt,"unknown reset outcome reuses persisted request UUID across restart");
    journal.Record("test-credit",UsageResetOutcome.NothingToReset);
    string nextAttempt=journal.GetOrCreate("test-credit");
    Check(nextAttempt!=attempt,"known non-consuming outcome permits a new logical attempt later");
    journal.Record("test-credit",UsageResetOutcome.Reset);
    Check(new UsageResetJournal(resetDirectory).GetOrCreate("test-credit")==nextAttempt,"consumed credit cannot get a new UUID");
}
finally
{
    if(Directory.Exists(resetDirectory)){foreach(string file in Directory.GetFiles(resetDirectory))File.Delete(file);Directory.Delete(resetDirectory);}
}
Console.WriteLine("PASS: reset journal survives restart, preserves unknown outcomes and renews only non-consuming attempts");
foreach(var local in new[]{"127.0.0.1","127.10.0.2","::1","::ffff:127.0.0.1","0.0.0.0","::"})Check(DevServerFilter.LocalEndpoint(local),"local/wildcard endpoint included");
foreach(var remote in new[]{"192.168.1.5","10.0.0.1","fe80::1","8.8.8.8","invalid"})Check(!DevServerFilter.LocalEndpoint(remote),"non-local-only binding excluded");
var devEvidence=new DevProcessEvidence("node",@"C:\Program Files\nodejs\node.exe","""node "C:\work\my project\node_modules\vite\bin\vite.js" --host""",@"C:\work\my project",true,"javascript");
Check(DevServerFilter.Classify(devEvidence)=="Vite","Vite with spaced project path");
Check(DevServerFilter.Classify(devEvidence with {InteractiveSession=false}) is null,"service session excluded");
Check(DevServerFilter.Classify(devEvidence with {ProjectRoot=""}) is null,"runtime alone without project proof excluded");
Check(DevServerFilter.Classify(devEvidence with {CommandLine="node C:/tools/mcp-server/index.js --stdio"}) is null,"MCP helper excluded");
Check(DevServerFilter.Classify(devEvidence with {CommandLine="node C:/vendor/language-server/server.js"}) is null,"language server excluded");
Check(DevServerFilter.Classify(devEvidence with {CommandLine="node C:/installed/vite/server.js"}) is null,"installed server outside project excluded");
Check(DevServerFilter.Classify(devEvidence with {ProcessName="svchost",CommandLine="svchost -k netsvcs"}) is null,"Windows service excluded");
Check(DevServerFilter.Classify(devEvidence with {ProcessName="python",CommandLine="python -m uvicorn app:app --reload",ProjectKind="python"})=="Python server","Python project dev server");
Check(DevServerFilter.Classify(devEvidence with {ProcessName="Api",Executable=@"C:\work\my project\bin\Debug\net8.0\Api.exe",CommandLine="Api.exe",ProjectKind="dotnet-web"})=="ASP.NET Core","compiled ASP.NET dev server");
Check(DevServerFilter.Classify(devEvidence with {ProcessName="MyNotch",Executable=@"C:\work\my project\bin\Debug\MyNotch.exe",CommandLine="MyNotch.exe",ProjectKind="dotnet"}) is null,"non-web desktop project webhook excluded");
Check(!DevServerFilter.EligibleProjectRoot(@"C:\work\app\node_modules\vite") && !DevServerFilter.EligibleProjectRoot(@"C:\Users\user\AppData\Roaming\tool"),"installed package roots excluded");
Check(DevServerFilter.Tokens("""node "C:\work\my project\server.js" --port 51234""")[1]==@"C:\work\my project\server.js","Windows argument quoting");
Check(DevServerFilter.DebuggerPort("node --inspect server.js",9229) && !DevServerFilter.DebuggerPort("node --inspect server.js",3000),"Node inspector port excluded without hiding app port");
Check(DevServerFilter.DebuggerPort("python -m debugpy --listen 127.0.0.1:5678 app.py",5678),"Python debugger port excluded");
Check(DevServerFilter.DebuggerPort("java -agentlib:jdwp=transport=dt_socket,server=y,address=*:5005 -jar app.jar",5005),"Java debugger port excluded");
Check(DevServerFilter.Classify(devEvidence with {CommandLine="node C:/Users/user/AppData/Roaming/npm/node_modules/vite/bin/vite.js"})=="Vite","global Vite CLI still allowed with verified project cwd");
Check(PortMonitorService.NetworkOrderToPort(0xB80B)==3000,"TCP port network byte order");
Console.WriteLine("PASS: localhost/wildcard filtering, project evidence, dev runtimes and excluded system/IDE/MCP services");
sealed class FakeUsageResetReader(UsageSnapshot snapshot) : IAgentUsageReader,IUsageResetter
{
    public int ResetCalls,ReadCalls;
    public TaskCompletionSource<UsageResetOutcome> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<UsageOutcome> ReadAsync(UsageProvider provider,CancellationToken ct)
    {
        ReadCalls++;
        return Task.FromResult(new UsageOutcome(snapshot with {Provider=provider,AvailableResets=ResetCalls>0?0:snapshot.AvailableResets,ResetCredits=ResetCalls>0?Array.Empty<UsageResetCredit>():snapshot.ResetCredits},null));
    }
    public Task<UsageResetOutcome> ResetCodexAsync(UsageResetCredit credit,CancellationToken ct){ResetCalls++;return Completion.Task;}
}
sealed class FakeUsageReader : IAgentUsageReader
{
    public Dictionary<UsageProvider,int> Calls {get;} = new();
    private readonly Dictionary<UsageProvider,TaskCompletionSource<UsageOutcome>> _pending = new();
    private int _cancelled;
    public int Cancelled=>_cancelled;
    public async Task<UsageOutcome> ReadAsync(UsageProvider provider,CancellationToken ct)
    {
        Calls[provider]=Calls.GetValueOrDefault(provider)+1;
        var completion=new TaskCompletionSource<UsageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[provider]=completion;
        try{return await completion.Task.WaitAsync(ct);}catch(OperationCanceledException){Interlocked.Increment(ref _cancelled);throw;}
    }
    public void Complete(UsageProvider provider,UsageOutcome outcome)=>_pending[provider].SetResult(outcome);
}
sealed class FakeHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
    public int Calls {get; private set;}
    public List<string> Urls {get;} = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested(); Urls.Add(request.RequestUri!.ToString());
        return Task.FromResult(respond(Calls++, request));
    }
}

sealed class ThrowingUsageReader : IAgentUsageReader
{
    public Task<UsageOutcome> ReadAsync(UsageProvider provider, CancellationToken cancellationToken) => throw new InvalidOperationException("unexpected");
}
