using MyNotch.Models;
using Windows.Foundation;
using Windows.Media.Control;

namespace MyNotch.Services;

/// <summary>
/// Event-driven wrapper over the System Media Transport Controls session manager
/// (PLAN Phase 4, PRD §3.5). No polling: subscribes to manager + session change
/// events and raises <see cref="TrackChanged"/> with a detached record.
/// Callbacks fire on background threads; subscribers marshal to the UI thread.
/// </summary>
public sealed class MediaControlService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _started;
    private bool _disposed;
    private readonly object _artworkLock = new();
    private ArtworkState _artwork = new ArtworkState.Idle();
    private long _refreshGeneration;
    private CancellationTokenSource? _artworkRecheck;
    private CancellationTokenSource? _transportResync;

    public event EventHandler<MediaTrackInfo>? TrackChanged;

    /// <summary>Position/rate report (seek, pause, resume). Raised on a background thread.</summary>
    public event EventHandler<MediaTimeline>? TimelineChanged;

    public bool IsRunning => _started && !_disposed;

    public async Task StartAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaControlService));
        if (_started) return;

        GlobalSystemMediaTransportControlsSessionManager manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] SMTC RequestAsync failed: {ex.Message}");
            TrackChanged?.Invoke(this, MediaTrackInfo.NoMedia);
            return;
        }

        _manager = manager;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        AttachSession(_manager.GetCurrentSession());
        _started = true;
        await RefreshAsync().ConfigureAwait(false);
    }

    public Task PlayPauseAsync() =>
        ControlAsync(static s => s.TryTogglePlayPauseAsync(), refreshArtwork: false);

    public Task NextAsync() =>
        ControlAsync(static s => s.TrySkipNextAsync());

    public Task PreviousAsync() =>
        ControlAsync(static s => s.TrySkipPreviousAsync());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _refreshGeneration);
        _started = false;
        DetachSession();
        if (_manager is not null)
        {
            try
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }
            catch { }
            _manager = null;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        try { AttachSession(sender.GetCurrentSession()); } catch { }
        _ = RefreshAsync();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) =>
        _ = RefreshAsync();

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        if (ReferenceEquals(sender, _session)) _ = RefreshAsync(refreshArtwork: true);
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        if (ReferenceEquals(sender, _session)) _ = RefreshAsync();
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        DetachSession();
        _session = session;
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        catch { }
    }

    private void DetachSession()
    {
        Interlocked.Increment(ref _refreshGeneration);
        lock (_artworkLock)
        {
            _transportResync?.Cancel();
            _transportResync?.Dispose();
            _transportResync = null;
            _artworkRecheck?.Cancel();
            _artworkRecheck?.Dispose();
            _artworkRecheck = null;
            _artwork = ArtworkLookup.Step(_artwork, new ArtworkInput.Reset()).State;
        }
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { }
        _session = null;
    }

    private bool IsCurrent(long generation, GlobalSystemMediaTransportControlsSession session) =>
        !_disposed && generation == Volatile.Read(ref _refreshGeneration) && ReferenceEquals(session, _session);

    private async Task RefreshAsync(bool refreshArtwork = false, bool allowRecheck = true)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var session = _session;
        if (session is null)
        {
            if (!_disposed) TrackChanged?.Invoke(this, MediaTrackInfo.NoMedia with { Revision = generation });
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            if (!IsCurrent(generation, session)) return;
            var playback = session.GetPlaybackInfo();
            var title = props?.Title ?? string.Empty;
            var sourceApp = session.SourceAppUserModelId ?? string.Empty;
            var (artist, album) = MediaMetadata.ArtistAlbum(sourceApp, props?.Artist ?? string.Empty, props?.AlbumTitle ?? string.Empty);
            var key = $"{sourceApp}\u001f{artist}\u001f{title}\u001f{album}";
            FetchArtwork? command;
            byte[]? thumbnail;
            lock (_artworkLock)
            {
                if (!IsCurrent(generation, session)) return;
                bool trackChanged = _artwork.Key != key;
                var transition = ArtworkLookup.Step(_artwork, new ArtworkInput.Request(key, refreshArtwork));
                _artwork = transition.State;
                command = transition.Command;
                thumbnail = _artwork.Bytes;
                if (trackChanged && allowRecheck)
                {
                    _artworkRecheck?.Cancel();
                    _artworkRecheck?.Dispose();
                    _artworkRecheck = new CancellationTokenSource();
                    _ = RecheckArtworkAsync(session, key, _artworkRecheck.Token);
                }
            }
            var track = new MediaTrackInfo
            {
                Revision = generation,
                Title = title,
                Artist = artist,
                Album = album,
                SourceApp = sourceApp,
                IsPlaying = playback?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                HasSession = true,
                Thumbnail = thumbnail,
                Timeline = ReadTimeline(session),
            };
            // Publish the new identity immediately. Never hold the previous song's cover
            // on screen while its replacement is still opening/decoding.
            if (!IsCurrent(generation, session)) return;
            TrackChanged?.Invoke(this, track);
            if (command is null) return;
            var bytes = await GetThumbnailAsync(props).ConfigureAwait(false);
            lock (_artworkLock)
            {
                if (!IsCurrent(generation, session)) return;
                _artwork = ArtworkLookup.Step(_artwork, new ArtworkInput.Completed(command.Attempt, bytes)).State;
                thumbnail = _artwork.Bytes;
            }
            if (!IsCurrent(generation, session)) return;
            TrackChanged?.Invoke(this, track with { Thumbnail = thumbnail, Timeline = ReadTimeline(session) });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] SMTC refresh failed: {ex.Message}");
            if (IsCurrent(generation, session)) TrackChanged?.Invoke(this, MediaTrackInfo.NoMedia with { Revision = generation });
        }
    }

    /// <summary>Fresh position report for the current session, or null when unavailable. Cheap; used to correct drift.</summary>
    public MediaTimeline? ReadCurrentTimeline()
    {
        var session = _session;
        if (session is null || _disposed) return null;
        try { return ReadTimeline(session); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] SMTC timeline read failed: {ex.Message}");
            return null;
        }
    }

    // Seek / pause / rate changes. Cheap: no thumbnail or property reads.
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        if (!ReferenceEquals(sender, _session) || _disposed) return;
        try { TimelineChanged?.Invoke(this, ReadTimeline(sender)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MyNotch] SMTC timeline failed: {ex.Message}"); }
    }

    private static MediaTimeline ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        var tl = session.GetTimelineProperties();
        if (tl is null) return MediaTimeline.Empty;
        var rate = session.GetPlaybackInfo()?.PlaybackRate ?? 1.0;
        return new MediaTimeline
        {
            Position = tl.Position,
            Duration = tl.EndTime - tl.StartTime,
            // Some players never stamp the report; treat that as "just now".
            ReportedAt = tl.LastUpdatedTime.Year < 2000 ? DateTimeOffset.UtcNow : tl.LastUpdatedTime,
            Rate = rate > 0 ? rate : 1.0,
        };
    }

    private async Task RecheckArtworkAsync(GlobalSystemMediaTransportControlsSession session, string key, CancellationToken ct)
    {
        try
        {
            // A single bounded refresh after a track change, not an idle polling loop.
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            lock (_artworkLock)
            {
                if (ct.IsCancellationRequested || _disposed || !ReferenceEquals(session, _session) || _artwork.Key != key) return;
            }
            await RefreshAsync(refreshArtwork: true, allowRecheck: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<byte[]?> GetThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props?.Thumbnail is not { } reference) return null;

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
            if (stream.Size is 0 or > 4 * 1024 * 1024) return null;
            using var reader = new Windows.Storage.Streams.DataReader(stream);
            var loaded = await reader.LoadAsync((uint)stream.Size).AsTask().ConfigureAwait(false);
            if (loaded != stream.Size) return null;
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] SMTC thumbnail failed: {ex.Message}");
        }
        return null;
    }

    private async Task ControlAsync(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> op, bool refreshArtwork = true)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var accepted = await op(session).AsTask(timeout.Token).ConfigureAwait(false);
            if (!accepted) System.Diagnostics.Debug.WriteLine("[MyNotch] Player declined the media command.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] SMTC control failed: {ex.Message}");
        }
        if (_disposed || !ReferenceEquals(session, _session)) return;
        lock (_artworkLock)
        {
            _transportResync?.Cancel();
            _transportResync?.Dispose();
            _transportResync = new CancellationTokenSource();
            _ = ResyncAfterTransportAsync(session, refreshArtwork, _transportResync.Token);
        }
        await RefreshAsync(refreshArtwork).ConfigureAwait(false);
    }

    private async Task ResyncAfterTransportAsync(GlobalSystemMediaTransportControlsSession session, bool refreshArtwork, CancellationToken ct)
    {
        try
        {
            // Like Oyenza, re-read the actual player state after its transition settles.
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || _disposed || !ReferenceEquals(session, _session)) return;
            await RefreshAsync(refreshArtwork).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
