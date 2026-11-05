using System.Net;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyNotch.Controls;
using MyNotch.Interop;
using MyNotch.Models;
using MyNotch.Services;
using MyNotch.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace MyNotch;

public sealed partial class MainWindow : Window
{
    // All sizes are device-independent pixels; WindowHelper scales them for the display's DPI.
    // The notch joins the screen edge at y=0 with concave shoulders, and
    // WindowPad of room around it for the shadow and the hover swell. The window hugs the shape plus that room.
    private static readonly SizeInt32 ExpandedShape = new(600, 200);
    // Usage fits its own cards; compact pages must not inherit this height.
    private const int UsagePanelMinimumHeight = 200;
    private int _usagePanelHeight = UsagePanelMinimumHeight;
    private int _activityHistoryPage;
    private int _actionPage;
    private const int ActivityPageSize = 2;
    public System.Collections.ObjectModel.ObservableCollection<AgentActivityItem> ActivityHistoryPage { get; } = new();
    private const double FloatGap = 0;
    private const double WindowPad = 28;
    private const float ExpandedRadius = 32;
    private const float PeekGrowX = 10;
    private const float PeekGrowY = 4;
    private static readonly TimeSpan ShapeSettleTime = TimeSpan.FromMilliseconds(520);

    private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ClipboardGlanceTime = TimeSpan.FromSeconds(8);

    public NotchViewModel ViewModel { get; } = new();
    public DeveloperToolsViewModel DevTools { get; } = new();
    public MediaViewModel Media { get; } = new();
    public ProductivityViewModel Focus { get; } = new();
    public CommandPaletteViewModel Palette { get; } = new();

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly WebhookServerService _webhook = new();
    private readonly ClipboardMonitorService _clipboard = new();
    private readonly PortMonitorService _ports = new();
    private readonly MediaControlService _media = new();
    private readonly LyricsService _lyricsService = new();
    private readonly TrayIconService _tray = new();
    private readonly PomodoroService _pomodoro;
    private readonly PrayerTimesService _prayer;
    private readonly AgentUsageService _usage = new(new AgentUsageReader());
    private readonly DispatcherQueueTimer _usageRefreshTimer;
    private readonly CommandPaletteService _commands = new();
    private readonly BeatAnimator _beat;
    private readonly IdleEyeAnimator _eyes;
    private readonly PulseAnimator _pulse;
    private readonly IslandVisuals _island;

    // One-shot / UI timers. None polls the OS: each is armed for a known moment or a visible countdown.
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _glanceTimer;
    private readonly DispatcherQueueTimer _headerClockTimer;
    private readonly DispatcherQueueTimer _prayerClockTimer;
    private readonly DispatcherQueueTimer _lyricTimer;
    private readonly DispatcherQueueTimer _progressTimer;
    private readonly DispatcherQueueTimer _settleTimer;

    private bool _ready;
    private bool _servicesStarted;
    private bool _webhookStarted;
    private bool _clipboardStarted;
    private bool _portsLoading;
    private bool _mediaStarted;
    private bool _prayerStarted;

    // Open/close behaviour.
    private enum HoverIntent { None, Open, Close }
    private HoverIntent _hoverIntent;
    private bool _pinned;       // opened by click/tray: stays until closed or focus is lost
    private bool _isActive;     // window has focus (a pinned notch that never got focus still closes on pointer exit)
    private bool _pointerInShape; // pointer is over the island's current shape (not just its transparent surroundings)
    private bool _peeking;        // the capsule is swelling in anticipation of opening



    // Glance (closed look) and the shape the island is morphing to.
    private GlanceKind _shownKind = GlanceKind.Clock;
    private SizeInt32 _shape;      // target shape of the island, in DIPs
    private SizeInt32 _windowDip;  // current window size, in DIPs (never smaller than the shape's stage)
    private GlanceState _glance = new(GlanceKind.Clock, string.Empty);
    private string? _noticeText;
    private DateTimeOffset _noticeUntil;
    private string? _clipLabel;
    private DateTimeOffset _clipUntil;
    private bool _clipboardPrimed;

    // Music / lyrics.
    private MediaTrackInfo _lastTrack = MediaTrackInfo.NoMedia;
    private MediaTimeline _timeline = MediaTimeline.Empty;
    private IReadOnlyList<LrcLine> _lyrics = Array.Empty<LrcLine>();
    private LyricsState _lyricsState = new LyricsState.Idle();
    private CancellationTokenSource? _lyricsCts;
    private byte[]? _artBytes;
    private long _artGeneration;
    private long _mediaRevision;

    public MainWindow()
    {
        InitializeComponent();
        App.TraceStartup("MainWindow XAML loaded");
        InitializeNotifications();
        App.TraceStartup("Notification inbox created");
        _pomodoro = new PomodoroService(_dispatcher);
        _prayer = new PrayerTimesService(_dispatcher);
        _beat = new BeatAnimator(Bar1, Bar2, Bar3);
        _eyes = new IdleEyeAnimator(IdleEyes);
        _pulse = new PulseAnimator(GlanceDot);
        _island = new IslandVisuals(Pill, ShadowHost, RimHost, LeftShoulder, RightShoulder, new Vector2(800, 560), StageFor(GlanceSizeFor(GlanceKind.Clock)).Width);

        _hoverTimer = CreateOneShot(OnHoverTick);
        _glanceTimer = CreateOneShot(RefreshGlance);
        _headerClockTimer = CreateOneShot(UpdateHeaderClock);
        _prayerClockTimer = _dispatcher.CreateTimer();
        _prayerClockTimer.Interval = TimeSpan.FromSeconds(1);
        _prayerClockTimer.Tick += (_, _) => { UpdatePrayerPage(); if (_prayer.Today is not null) _ = _prayer.RefreshIfStaleAsync(); };
        var placeMenu = new MenuFlyout();
        foreach (var place in PrayerLocation.Places)
        {
            var item = new MenuFlyoutItem { Text = $"{place.DisplayCity} \u00b7 {place.ZoneLabel}" };
            item.Click += async (_, _) => await _prayer.SelectLocationAsync(place);
            placeMenu.Items.Add(item);
        }
        PrayerPlaceButton.Flyout = placeMenu;
        _usageRefreshTimer = _dispatcher.CreateTimer();
        _usageRefreshTimer.Interval = TimeSpan.FromMinutes(2);
        _usageRefreshTimer.IsRepeating = true;
        _usageRefreshTimer.Tick += (_, _) => _ = _usage.RefreshAsync();
        _usage.Changed += OnUsageChanged;
        var servicesMenu = new MenuFlyout();
        foreach (var provider in Enum.GetValues<UsageProvider>())
        {
            var item = new ToggleMenuFlyoutItem { Text = UsagePresentation.Name(provider), IsChecked = true };
            item.Click += (_, _) => SetUsageProviderEnabled(provider, item.IsChecked); // persisted in settings.json
            servicesMenu.Items.Add(item);
            _serviceItems[provider] = item;
        }
        UsageServicesButton.Flyout = servicesMenu;
        SyncUsageCards();
        _lyricTimer = CreateOneShot(UpdateLyricState);
        _settleTimer = CreateOneShot(OnShapeSettled);
        _progressTimer = _dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(1);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += (_, _) => UpdateProgress();

        _shape = GlanceSizeFor(GlanceKind.Clock);
        _windowDip = StageFor(_shape);
        WindowHelper.ConfigureNotchShell(this, _windowDip);
        _island.Snap(new Vector2(_shape.Width, _shape.Height), 14f);
        GlanceHost.Width = _shape.Width;
        GlanceHost.Height = _shape.Height;

        // Pressing on the island pins it open; buttons mark pointer events handled, so listen for those too.
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);
        Pill.SizeChanged += (_, e) => _island.SetStageWidth((float)e.NewSize.Width); // keeps the shape centred when the window resizes

        AgentAlert.AlertClicked += OnAlertClicked;
        DevTools.PropertyChanged += OnDevToolsChanged;
        Media.PropertyChanged += OnMediaChanged;
        _pomodoro.Changed += OnPomodoroChanged;
        _pomodoro.PhaseCompleted += OnPomodoroPhaseCompleted;
        Focus.ApplyPomodoro(_pomodoro);
        _prayer.ScheduleChanged += OnPrayerScheduleChanged;
        _prayer.ReminderDue += OnPrayerReminderDue;
        _tray.OpenRequested += OnTrayOpen;
        _tray.PaletteRequested += OnTrayPalette;
        _tray.QuitRequested += OnTrayQuit;
        RootGrid.Loaded += OnRootLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
        InitializeAgents();
        InitializeDevExtras();
        InitializeFeatures();

        _ready = true;
        SyncAgentsPage();
        RefreshGlance();
    }

    private DispatcherQueueTimer CreateOneShot(Action onTick)
    {
        var timer = _dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => onTick();
        return timer;
    }

    // ---------------------------------------------------------------- startup wiring

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_isActive)
        {
            // Clicked away from a pinned notch: tuck it back in.
            if (ViewModel.IsExpanded && _pinned) Collapse();
            return;
        }

        // Re-assert capture exclusion (affinity can be dropped on re-activation).
        WindowHelper.ExcludeFromCapture(WindowHelper.GetHwnd(this));
    }

    // Services start when the content first loads, not on first activation: Windows' foreground lock
    // can keep a freshly launched tool window from ever being activated, and nothing should wait on that.
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_servicesStarted) return;
        _servicesStarted = true;

        var hwnd = WindowHelper.GetHwnd(this);
        App.TraceStartup("MainWindow root loaded");
        if (!_webhookStarted)
        {
            _webhookStarted = true;
            _webhook.PayloadReceived += OnPayloadReceived;
            _ = StartWebhookAsync();
        }
        if (!_clipboardStarted)
        {
            _clipboardStarted = true;
            _clipboard.ClipboardChanged += OnClipboardChanged;
            try
            {
                _clipboard.Start(hwnd);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MyNotch] Clipboard monitor failed: {ex.Message}");
            }
            _ = FetchClipboardAsync();
        }
        if (!_mediaStarted)
        {
            _mediaStarted = true;
            _media.TrackChanged += OnTrackChanged;
            _media.TimelineChanged += OnTimelineChanged;
            _ = _media.StartAsync();
        }
        if (!_prayerStarted)
        {
            _prayerStarted = true;
            _ = _prayer.StartAsync();
        }
        _ = _tray.Start(hwnd);
        StartNotifications(hwnd);
        StartFeatures(hwnd);
        App.TraceStartup("Notification watchers started");
    }

    private async Task StartWebhookAsync()
    {
        try
        {
            await _webhook.StartAsync().ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                var line = $"Webhook listening on 127.0.0.1:{WebhookServerService.Port}";
                _tray.InfoLine = line;
                if (ViewModel.StatusText == "MyNotch") ViewModel.StatusText = line;
            });
        }
        catch (HttpListenerException ex)
        {
            // Loopback listener blocked (e.g. missing URL reservation on a locked-down machine).
            _dispatcher.TryEnqueue(() => ViewModel.StatusText =
                $"Webhook unavailable ({ex.Message}). Once: netsh http add urlacl url=http://127.0.0.1:{WebhookServerService.Port} user=Everyone");
        }
    }

    // ---------------------------------------------------------------- agents (webhook)

    // HttpListener events fire on listener threads (AGENTS.md): everything below is marshalled to the UI thread.
    private void OnPayloadReceived(object? sender, AgentPayload payload) =>
        _dispatcher.TryEnqueue(() => ApplyPayload(payload));

    private void ApplyPayload(AgentPayload payload)
    {
        AgentAlert.Apply(payload);
        TrackSession(payload);
        _activityHistoryPage = 0;
        SyncAgentsPage();
        RefreshGlance();
    }

    private void SyncAgentsPage()
    {
        bool hasAlert = AgentAlert.ViewModel.HasAlert;
        AgentAlert.Visibility = hasAlert ? Visibility.Visible : Visibility.Collapsed;
        AgentsEmpty.Visibility = hasAlert ? Visibility.Collapsed : Visibility.Visible;
        ActivityPager.Visibility = hasAlert ? Visibility.Visible : Visibility.Collapsed;
        SyncActivityHistory();
        UpdateUsagePanelHeight();
        UpdateBadges();
    }

    private void OnUsageChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() => { SyncUsageCards(); CheckQuotaNotifications(); });
    private void SyncUsageCards()
    {
        var cards = new (UsageProvider Provider, UsageCardControl Card)[]
        {
            (UsageProvider.Codex, CodexUsageCard), (UsageProvider.Claude, ClaudeUsageCard),
            (UsageProvider.Antigravity, AntigravityUsageCard), (UsageProvider.Muse, MuseUsageCard),
        };
        foreach (var (provider, card) in cards)
        {
            card.Visibility = _usage.IsEnabled(provider) ? Visibility.Visible : Visibility.Collapsed;
            card.Apply(provider, _usage.GetState(provider));
        }
        var updated = cards.Select(c => _usage.GetState(c.Provider).Snapshot?.AsOf).Where(t => t is not null).Max();
        UsageUpdatedLabel.Text = updated is { } at ? $"Updated {at.LocalDateTime:HH:mm:ss}" : "Not updated yet";
        bool loading = cards.Any(c => _usage.GetState(c.Provider).Content is UsageContent.Loading);
        UsageRefreshButton.Content = loading ? "Checking..." : "Refresh";
        UsageRefreshButton.IsEnabled = !loading && !_usage.IsResetting;
        CodexUsageCard.SetResetting(_usage.IsResetting);
        UsageResetStatus.Text = _usage.ResetStatus;
        UsageResetStatus.Visibility = string.IsNullOrEmpty(_usage.ResetStatus) ? Visibility.Collapsed : Visibility.Visible;
        UsageEmpty.Visibility = cards.All(c => !_usage.IsEnabled(c.Provider)) ? Visibility.Visible : Visibility.Collapsed;
        UpdateUsagePanelHeight();
    }
    private async void CodexResetRequested(object? sender, EventArgs e) => await _usage.ResetCodexAsync();
    private async void UsageRefresh_Click(object sender, RoutedEventArgs e) => await _usage.RefreshAsync();

    private void SyncActivityHistory()
    {
        var history = AgentAlert.ViewModel.History;
        int pages = Math.Max(1, (history.Count + ActivityPageSize - 1) / ActivityPageSize);
        _activityHistoryPage = Math.Clamp(_activityHistoryPage, 0, pages - 1);
        ActivityHistoryPage.Clear();
        foreach (var item in history.Skip(_activityHistoryPage * ActivityPageSize).Take(ActivityPageSize)) ActivityHistoryPage.Add(new AgentActivityItem(item));
        RecentHeader.Text = $"RECENT - {_activityHistoryPage + 1}/{pages}";
        PreviousActivityPage.IsEnabled = _activityHistoryPage > 0;
        NextActivityPage.IsEnabled = _activityHistoryPage + 1 < pages;
    }
    private void PreviousActivityPage_Click(object sender, RoutedEventArgs e) { _activityHistoryPage--; SyncActivityHistory(); UpdateUsagePanelHeight(); }
    private void NextActivityPage_Click(object sender, RoutedEventArgs e) { _activityHistoryPage++; SyncActivityHistory(); UpdateUsagePanelHeight(); }
    private void AgentsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) UpdateUsagePanelHeight();
    }
    private void UpdateUsagePanelHeight()
    {
        // Measure all rows at the final content width, including while another tab is shown.
        AgentsPage.Measure(new Windows.Foundation.Size(600 - 44, double.PositiveInfinity));
        int height = Math.Max(UsagePanelMinimumHeight, (int)Math.Ceiling(AgentsPage.DesiredSize.Height + 56));
        if (_usagePanelHeight == height) return;
        _usagePanelHeight = height;
        if (_ready) SyncExpandedSize();
    }

    private void OnAlertClicked(object? sender, AgentPayload payload) =>
        WindowHelper.FocusSender(payload.Hwnd, payload.Pid);

    // ---------------------------------------------------------------- clipboard / dev tools

    // Clipboard subclass callback runs on the UI thread; content fetch is async WinRT.
    private void OnClipboardChanged(object? sender, EventArgs e) => _ = FetchClipboardAsync();

    private async Task FetchClipboardAsync()
    {
        string text;
        bool record;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;
            // Password managers mark secrets so clipboard tools leave them alone; honour that.
            if (content.Contains("ExcludeClipboardContentFromMonitorProcessing") || content.Contains("Clipboard Viewer Ignore"))
                return;
            record = !content.Contains("CanIncludeInClipboardHistory");
            text = await content.GetTextAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Clipboard read failed: {ex.Message}");
            return;
        }
        _dispatcher.TryEnqueue(() =>
        {
            DevTools.ApplyClipboardText(text);
            if (_clipboardPrimed && record) RecordClip(text);
            _clipboardPrimed = true; // whatever was already on the clipboard at startup stays quiet
        });
    }

    private void OnDevToolsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnDevToolsChanged(sender, e));
            return;
        }
        if (e.PropertyName == nameof(DeveloperToolsViewModel.Detection) && _clipboardPrimed)
        {
            if (DevTools.HasClipboardAction)
            {
                _clipLabel = DevTools.ClipboardActionLabel;
                _clipUntil = DateTimeOffset.Now + ClipboardGlanceTime;
            }
            else
            {
                _clipLabel = null;
            }
            RefreshGlance();
        }
        UpdateBadges();
    }

    private async Task RefreshPortsAsync()
    {
        if (_portsLoading) return;
        _portsLoading = true;
        _dispatcher.TryEnqueue(() => DevTools.BeginRefresh());
        try
        {
            var list = await Task.Run(() => _ports.Refresh()).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                DevTools.SetPorts(list);
                DevTools.EndRefresh(true);
                Bindings.Update();
                // Repositories are found through the servers' project folders, so refresh them once ports are known.
                if (ViewModel.IsExpanded && ViewModel.SelectedTab == NotchTab.Dev) _ = RefreshDevExtrasAsync();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => DevTools.SetPortsError($"Port scan failed: {ex.Message}"));
        }
        finally
        {
            _portsLoading = false;
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        _devExtrasAt = DateTimeOffset.MinValue; // an explicit refresh also re-reads containers and repositories
        _ = RefreshPortsAsync();
    }

    private async void CopyFormatted_Click(object sender, RoutedEventArgs e)
    {
        var formatted = DevTools.Detection.Formatted;
        if (string.IsNullOrEmpty(formatted)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(formatted);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Clipboard copy failed: {ex.Message}");
        }
    }

    private async void KillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var selected = DevTools.Ports.FirstOrDefault(p => p.StopToken == token);
        if (selected is null) return;
        int pid = selected.Pid;
        var (ok, error) = await Task.Run(() =>
        {
            var success = _ports.TryStop(selected, out var err);
            return (success, err);
        }).ConfigureAwait(false);
        _dispatcher.TryEnqueue(() =>
        {
            if (ok) DevTools.RemovePid(pid);
            else DevTools.SetPortsError($"Stop {pid} failed: {error}");
            Bindings.Update();
        });
    }

    // ---------------------------------------------------------------- media + lyrics

    // SMTC callbacks fire on background threads.
    private void OnTrackChanged(object? sender, MediaTrackInfo track) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (track.Revision < _mediaRevision) return;
            _mediaRevision = track.Revision;
            ApplyTrack(track);
        });

    private void OnTimelineChanged(object? sender, MediaTimeline timeline) =>
        _dispatcher.TryEnqueue(() =>
        {
            bool gainedDuration = !_timeline.IsKnown && timeline.IsKnown;
            _timeline = timeline;
            if (gainedDuration && _lastTrack.HasSession &&
                _lyricsState is LyricsState.Finished { Result: LyricsResult.Missing or LyricsResult.Failed }
                    or LyricsState.Waiting)
                _ = LoadLyricsAsync(_lastTrack);
            UpdateLyricState();
            UpdateProgress();
        });

    private void ApplyTrack(MediaTrackInfo track)
    {
        bool newSong = !_lastTrack.IsSameTrack(track);
        _lastTrack = track;
        _timeline = track.Timeline;
        Media.Apply(track);

        if (!ReferenceEquals(track.Thumbnail, _artBytes))
            _ = SetArtAsync(track.Thumbnail);

        if (newSong)
        {
            _lyricsCts?.Cancel();
            _lyricsState = LyricsLookup.Step(_lyricsState, new LyricsInput.Reset()).State;
            _lyrics = Array.Empty<LrcLine>();
            Media.SetLyric(string.Empty, string.Empty);
            Media.SetPreviousLine(string.Empty);
            Media.SetLyricsAvailability(LyricsAvailability.Missing, "Lyrics will appear here");
            if (track.HasSession) _ = LoadLyricsAsync(track);
        }

        UpdateLyricState();
        UpdateProgress();
        SyncProgressTimer();
        RefreshGlance();
    }

    private async Task SetArtAsync(byte[]? bytes)
    {
        var generation = ++_artGeneration;
        _artBytes = bytes;
        ImageSource? image = null;
        if (bytes is not null)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                var bitmap = new BitmapImage { DecodePixelWidth = 256 };
                await bitmap.SetSourceAsync(stream);
                image = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MyNotch] Album art decode failed: {ex.Message}");
            }
        }

        if (generation != _artGeneration) return;
        Media.SetArt(image);
        GlanceArtImage.Source = image;
        RefreshGlance();
    }

    private Task LoadLyricsAsync(MediaTrackInfo track)
    {
        var transition = LyricsLookup.Step(_lyricsState,
            new LyricsInput.Request(new LyricsQuery(track.Artist, track.Title, _timeline.Duration) { Album = track.Album }));
        _lyricsState = transition.State;
        return ExecuteLyricsFetchAsync((FetchLyrics)transition.Command!);
    }

    private async Task ExecuteLyricsFetchAsync(FetchLyrics command)
    {
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _lyricsCts = cancellation;
        var ct = cancellation.Token;
        Media.SetLyricsAvailability(LyricsAvailability.Loading, "Finding lyrics...");
        LyricsResult result;
        try { result = await _lyricsService.GetAsync(command.Query, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        var completed = LyricsLookup.Step(_lyricsState, new LyricsInput.Completed(command.Attempt, result));
        if (ReferenceEquals(completed.State, _lyricsState)) return;
        _lyricsState = completed.State;
        if (command.Query.Duration <= TimeSpan.Zero && _timeline.IsKnown && result is LyricsResult.Missing)
        {
            _ = LoadLyricsAsync(_lastTrack);
            return;
        }
        _lyrics = result is LyricsResult.Synced synced ? synced.Lines : Array.Empty<LrcLine>();
        if (completed.Command is ScheduleLyricsRetry retry)
        {
            Media.SetLyricsAvailability(LyricsAvailability.Loading, "Finding lyrics...");
            _ = RetryLyricsAutomaticallyAsync(retry, ct);
            return;
        }
        switch (result)
        {
            case LyricsResult.Synced:
                Media.SetLyricsAvailability(LyricsAvailability.Synced, "Synced lyrics"); break;
            case LyricsResult.Plain plain:
                Media.SetLyricsAvailability(LyricsAvailability.Plain, "Lyrics · not synced", plain.Text); break;
            case LyricsResult.Instrumental:
                Media.SetLyricsAvailability(LyricsAvailability.Instrumental, "Instrumental track"); break;
            case LyricsResult.Missing:
                Media.SetLyricsAvailability(LyricsAvailability.Missing, "No lyrics found for this track"); break;
            case LyricsResult.Failed failed:
                Media.SetLyricsAvailability(LyricsAvailability.Failed, failed.Message); break;
        }
        UpdateLyricState();
    }

    private async Task RetryLyricsAutomaticallyAsync(ScheduleLyricsRetry command, CancellationToken ct)
    {
        try
        {
            await Task.Delay(command.Delay, ct).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested) return;
                var transition = LyricsLookup.Step(_lyricsState, new LyricsInput.RetryDue(command.Attempt));
                _lyricsState = transition.State;
                if (transition.Command is FetchLyrics fetch) _ = ExecuteLyricsFetchAsync(fetch);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // Shows the current + next line and arms a one-shot timer for the moment the next line starts.
    private void UpdateLyricState()
    {
        _lyricTimer.Stop();
        if (_lyrics.Count == 0 || !_lastTrack.HasSession)
        {
            if (Media.HasLyrics) { Media.SetLyric(string.Empty, string.Empty); RefreshGlance(); }
            return;
        }

        var position = _timeline.PositionNow(_lastTrack.IsPlaying);
        int index = LrcParser.IndexAt(_lyrics, position);
        var current = index >= 0 ? _lyrics[index].Text : string.Empty;
        if (index >= 0 && current.Length == 0) current = "\u266a";
        var next = index + 1 < _lyrics.Count ? _lyrics[index + 1].Text : string.Empty;
        Media.SetLyric(current, next);
        Media.SetPreviousLine(index > 0 ? _lyrics[index - 1].Text : string.Empty);
        if (index < 0) Media.SetLyric("♪", next);

        if (_lastTrack.IsPlaying && index + 1 < _lyrics.Count)
        {
            var wait = (_lyrics[index + 1].Time - position) / Math.Max(0.1, _timeline.Rate);
            _lyricTimer.Interval = wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50);
            _lyricTimer.Start();
        }
        RefreshGlance();
    }

    private void UpdateProgress()
    {
        var position = _timeline.PositionNow(_lastTrack.IsPlaying);
        Media.SetPosition(_timeline.IsKnown ? position : TimeSpan.Zero, _timeline.Duration);
        if (!_timeline.IsKnown)
        {
            Media.SetProgress(0);
            return;
        }
        Media.SetProgress(position / _timeline.Duration);
    }

    // The progress bar only ticks while it can actually be seen.
    private void SyncProgressTimer()
    {
        bool visible = ViewModel.IsExpanded && ViewModel.SelectedTab == NotchTab.Music && _lastTrack is { HasSession: true, IsPlaying: true };
        if (visible && !_progressTimer.IsRunning) { UpdateProgress(); _progressTimer.Start(); }
        else if (!visible && _progressTimer.IsRunning) _progressTimer.Stop();
    }

    private void OnMediaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnMediaChanged(sender, e));
            return;
        }
        // Every x:Bind on Media is OneWay, so the view model's own notifications are enough. A full
        // Bindings.Update() here re-ran ~50 bindings per property change (several times a second while playing).
        if (e.PropertyName == nameof(MediaViewModel.HasMedia) && ViewModel.SelectedTab == NotchTab.Music) SyncExpandedSize();
    }

    private void MediaPlayPause_Click(object sender, RoutedEventArgs e) => _ = _media.PlayPauseAsync();
    private void MediaNext_Click(object sender, RoutedEventArgs e) => _ = _media.NextAsync();
    private void MediaPrevious_Click(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    // ---------------------------------------------------------------- focus + prayer

    private void PomodoroStartFocus_Click(object sender, RoutedEventArgs e) => _pomodoro.StartFocus();
    private void PomodoroStartBreak_Click(object sender, RoutedEventArgs e) => _pomodoro.StartBreak();
    private void PomodoroToggle_Click(object sender, RoutedEventArgs e) => _pomodoro.Toggle();
    private void PomodoroReset_Click(object sender, RoutedEventArgs e) => _pomodoro.Reset();
    private void PomodoroSkip_Click(object sender, RoutedEventArgs e) => _pomodoro.Skip();
    private void PomodoroLongBreak_Click(object sender, RoutedEventArgs e) => _pomodoro.Select(FocusMode.LongBreak);
    private void PomodoroSelectFocus_Click(object sender, RoutedEventArgs e) => _pomodoro.Select(FocusMode.Focus);
    private void PomodoroSelectBreak_Click(object sender, RoutedEventArgs e) => _pomodoro.Select(FocusMode.ShortBreak);
    private void PomodoroStop_Click(object sender, RoutedEventArgs e) => _pomodoro.Stop();

    private void OnPomodoroChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() =>
        {
            Focus.ApplyPomodoro(_pomodoro);
            FocusRingProgress.StrokeDashArray = new DoubleCollection { Focus.Progress * 67.23, 100 };
            FocusModeButton.Background = (Brush)Application.Current.Resources[_pomodoro.State.Mode == FocusMode.Focus ? "NotchSurfaceActiveBrush" : "NotchSurfaceBrush"];
            ShortBreakModeButton.Background = (Brush)Application.Current.Resources[_pomodoro.State.Mode == FocusMode.ShortBreak ? "NotchSurfaceActiveBrush" : "NotchSurfaceBrush"];
            LongBreakModeButton.Background = (Brush)Application.Current.Resources[_pomodoro.State.Mode == FocusMode.LongBreak ? "NotchSurfaceActiveBrush" : "NotchSurfaceBrush"];
            FocusModeButton.Content = $"Focus {_pomodoro.Config.Focus.TotalMinutes:0}";
            ShortBreakModeButton.Content = $"Short {_pomodoro.Config.ShortBreak.TotalMinutes:0}";
            LongBreakModeButton.Content = $"Long {_pomodoro.Config.LongBreak.TotalMinutes:0}";
            RefreshGlance();
        });

    private void OnPomodoroPhaseCompleted(object? sender, PomodoroPhase finished) =>
        _dispatcher.TryEnqueue(() => ShowNotice(
            finished == PomodoroPhase.Focus ? "Focus done — break time" : "Break over — ready to focus",
            TimeSpan.FromSeconds(10)));

    private void OnPrayerScheduleChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() =>
        {
            UpdatePrayerPage();
            RefreshGlance();
            Bindings.Update();
        });

    private void UpdatePrayerPage()
    {
        var place = _prayer.Location;
        var now = place.LocalNow(DateTimeOffset.Now);
        var next = _prayer.NextCityTime;
        PrayerCity.Text = place.DisplayCity;
        PrayerLocationDetail.Text = $"Indonesia \u00b7 {place.ZoneLabel}";
        PrayerNextLabel.Text = PrayerPresentation.DisplayName(_prayer.NextPrayerName);
        PrayerCountdown.Text = PrayerPresentation.Countdown(now, next);
        PrayerAt.Text = next is { } at ? $"{at:HH:mm}\n{(at.Date > now.Date ? "Tomorrow" : "Today")}" : "";
        PrayerStatus.Text = _prayer.Status;
        PrayerSunrise.Text = _prayer.Today is { } d ? $"Sunrise {d.Sunrise:HH:mm}" : "Sunrise --:--";
        PrayerImsak.Text = _prayer.Today?.Imsak is { } imsak ? $"Imsak {imsak:HH:mm}" : "Imsak --:--";
        PrayerQibla.Text = $"Qibla {place.QiblaBearing.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}\u00b0";
        var day = _prayer.Today;
        TimeOnly[] times = day is null ? [] : [day.Fajr, day.Dhuhr, day.Asr, day.Maghrib, day.Isha];
        TextBlock[] labels = [PrayerLabel0, PrayerLabel1, PrayerLabel2, PrayerLabel3, PrayerLabel4];
        TextBlock[] clocks = [PrayerTime0, PrayerTime1, PrayerTime2, PrayerTime3, PrayerTime4];
        Microsoft.UI.Xaml.Shapes.Ellipse[] nodes = [PrayerNode0, PrayerNode1, PrayerNode2, PrayerNode3, PrayerNode4];
        string[] names = ["Fajr", "Dhuhr", "Asr", "Maghrib", "Isha"];
        for (int i = 0; i < 5; i++)
        {
            bool active = day is not null && names[i] == _prayer.NextPrayerName;
            labels[i].Foreground = new SolidColorBrush(active ? Windows.UI.Color.FromArgb(255, 105, 173, 255) : Windows.UI.Color.FromArgb(255, 136, 139, 147));
            nodes[i].Fill = labels[i].Foreground;
            clocks[i].Text = day is null ? "--:--" : times[i].ToString("HH:mm");
        }
        double progress = day is null ? 0 : PrayerPresentation.Progress(day, now);
        var point = PrayerPresentation.ArcPoint(progress);
        PrayerArcClip.Rect = new Windows.Foundation.Rect(0, 0, day is null ? 0 : point.X, 78);
        // Map the curve coordinates into its host; the icon itself stays square in DIPs.
        double markerX = point.X * PrayerArcHost.ActualWidth / 300;
        double markerY = point.Y * PrayerArcHost.ActualHeight / 78;
        Canvas.SetLeft(PrayerSkyMarker, markerX - 12); Canvas.SetTop(PrayerSkyMarker, markerY - 12);
        Canvas.SetLeft(PrayerSunHalo, markerX - 14); Canvas.SetTop(PrayerSunHalo, markerY - 14);
        Canvas.SetLeft(PrayerMarkerBacking, markerX - 10); Canvas.SetTop(PrayerMarkerBacking, markerY - 10);
        PrayerSkyMarker.Visibility = PrayerSunHalo.Visibility = PrayerMarkerBacking.Visibility = day is null ? Visibility.Collapsed : Visibility.Visible;
        var period = PrayerPresentation.DayPeriod(now);
        var glow = period switch
        {
            PrayerDayPeriod.Morning => Windows.UI.Color.FromArgb(80, 255, 217, 138),
            PrayerDayPeriod.Midday => Windows.UI.Color.FromArgb(80, 255, 210, 74),
            PrayerDayPeriod.Afternoon => Windows.UI.Color.FromArgb(80, 255, 138, 92),
            _ => Windows.UI.Color.FromArgb(80, 185, 174, 247),
        };
        PrayerGlowCenter.Color = glow;
        glow.A = 25;
        PrayerGlowMiddle.Color = glow;
        PrayerMorning.Visibility = period == PrayerDayPeriod.Morning ? Visibility.Visible : Visibility.Collapsed;
        PrayerSun.Visibility = period == PrayerDayPeriod.Midday ? Visibility.Visible : Visibility.Collapsed;
        PrayerAfternoon.Visibility = period == PrayerDayPeriod.Afternoon ? Visibility.Visible : Visibility.Collapsed;
        PrayerMoon.Visibility = period == PrayerDayPeriod.Night ? Visibility.Visible : Visibility.Collapsed;
        string periodLabel = period switch
        {
            PrayerDayPeriod.Morning => "Pagi (05:00-10:59)",
            PrayerDayPeriod.Midday => "Siang (11:00-14:59)",
            PrayerDayPeriod.Afternoon => "Sore (15:00-17:59)",
            _ => "Malam (18:00-04:59)",
        };
        ToolTipService.SetToolTip(PrayerSkyMarker, $"{periodLabel} - {place.ZoneLabel}");

    }

    private void PrayerArcHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready) UpdatePrayerPage();
    }

    private void OnPrayerReminderDue(object? sender, string prayer) =>
        _dispatcher.TryEnqueue(() => ShowNotice($"{prayer} prayer time", TimeSpan.FromSeconds(30)));

    private void ShowNotice(string text, TimeSpan duration)
    {
        _notifications.Post(new(text, NotificationKind.General, text, "", DateTimeOffset.Now, VisibleFor: duration));
    }

    // ---------------------------------------------------------------- glance (closed look)

    // Closed notch sizes with 14 DIP bottom corners and a straight top edge.
    private static SizeInt32 GlanceSizeFor(GlanceKind kind) => kind switch
    {
        GlanceKind.Music => new SizeInt32(304, 38),
        GlanceKind.Focus => new SizeInt32(196, 38),
        GlanceKind.Agent => new SizeInt32(272, 38),
        GlanceKind.Clipboard => new SizeInt32(262, 38),
        GlanceKind.Notice => new SizeInt32(380, 52),
        GlanceKind.Prayer => new SizeInt32(212, 38),
        GlanceKind.Meeting => new SizeInt32(300, 38),
        _ => new SizeInt32(150, 30),
    };

    // Circumference of the focus ring's path, in units of its stroke thickness: pi * (24 - 2.5) / 2.5.
    private const double RingUnits = 27.02;

    private GlanceInputs BuildInputs()
    {
        var (agent, otherAgents) = AgentForGlance();
        return BuildInputs(agent, otherAgents);
    }

    private GlanceInputs BuildInputs(AgentPayload? agent, int otherAgents) => new()
    {
        Now = DateTimeOffset.Now,
        Agent = agent,
        OtherAgents = otherAgents,
        NextMeeting = _meetings?.Next,
        MeetingLead = _meetings?.Lead ?? TimeSpan.FromMinutes(10),
        NoticeText = _noticeText,
        NoticeUntil = _noticeUntil,
        ClipboardLabel = _clipLabel,
        ClipboardUntil = _clipUntil,
        Phase = _pomodoro.Phase,
        PhaseRemaining = _pomodoro.Remaining,
        MusicPlaying = Media.IsPlaying,
        MusicText = CurrentMusicGlance.Text,
        NextPrayerName = _prayer.NextPrayerName,
        NextPrayerAt = _prayer.NextPrayerTime,
    };

    private MusicGlance CurrentMusicGlance => MusicGlance.Resolve(
        Media.HasSyncedLyrics, Media.CurrentLine, Media.NextLine);

    // Re-decides what the closed notch shows, and arms one timer for the next time-based change.
    private void RefreshGlance()
    {
        if (!_ready) return;
        var result = GlanceResolver.Resolve(BuildInputs());
        _glance = result.State;
        UpdateBadges();

        if (ViewModel.IsExpanded)
        {
            _glanceTimer.Stop();
            return;
        }

        var wait = result.NextChange - DateTimeOffset.Now;
        _glanceTimer.Stop();
        _glanceTimer.Interval = wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50);
        _glanceTimer.Start();

        ShowGlance(_glance);
    }

    private void ShowGlance(GlanceState g)
    {
        var size = GlanceSizeFor(g.Kind);
        bool kindChanged = g.Kind != _shownKind;

        bool isClock = g.Kind == GlanceKind.Clock;
        bool isMusic = g.Kind == GlanceKind.Music;
        bool isLyric = isMusic && CurrentMusicGlance.IsLyric;
        bool lyricChanged = isLyric && GlanceText.Text != g.Text;
        if (isLyric) size = new SizeInt32(380, 48);
        else if (isMusic) size = new SizeInt32(130, 38); // no synced lyric: artwork and beat bars only
        bool isFocus = g.Kind == GlanceKind.Focus && _pomodoro.IsRunning;
        bool hasArt = isMusic && Media.HasArt;
        bool awaiting = g.Kind == GlanceKind.Agent && g.Agent == AgentStatus.AwaitingApproval;
        var notice = g.Kind == GlanceKind.Notice ? _notifications.Current : null;
        bool isVolume = notice?.Kind == NotificationKind.Volume;
        if (isVolume) size = new SizeInt32(240, 38);

        GlanceHost.ColumnSpacing = isClock || isVolume ? 0 : 9;
        GlanceText.HorizontalAlignment = isClock ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        GlanceText.Text = g.Text;
        GlanceText.Visibility = isClock || isVolume ? Visibility.Collapsed : Visibility.Visible;
        GlanceVolume.Visibility = isVolume ? Visibility.Visible : Visibility.Collapsed;
        if (isVolume)
        {
            GlanceVolumeLevel.Value = notice!.Level ?? 0;
            GlanceVolumeMinus.Glyph = notice.Level == 0 ? "" : ""; // Mute : Remove (minus)
        }
        IdleEyes.Visibility = isClock ? Visibility.Visible : Visibility.Collapsed;
        if (isClock) _eyes.Start(); else _eyes.Stop();
        GlanceText.FontSize = isLyric ? 12.5 : 11.5;
        GlanceText.MaxLines = isLyric || g.Kind == GlanceKind.Notice ? 2 : 1;
        GlanceText.TextWrapping = isLyric || g.Kind == GlanceKind.Notice ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (lyricChanged) NotchMotion.FadeIn(GlanceText, riseDip: 0f, durationMs: 130);
        GlanceSub.Text = g.Sub;
        GlanceSub.Visibility = string.IsNullOrEmpty(g.Sub) ? Visibility.Collapsed : Visibility.Visible;

        GlanceArt.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        GlanceRing.Visibility = isFocus ? Visibility.Visible : Visibility.Collapsed;
        bool hasGlyph = g.Kind is GlanceKind.Notice or GlanceKind.Meeting or GlanceKind.Clipboard;
        bool isPrayer = g.Kind == GlanceKind.Prayer;
        GlanceDot.Visibility = !isClock && !hasArt && !isFocus && !hasGlyph && !isPrayer ? Visibility.Visible : Visibility.Collapsed;
        GlanceNoticeIcon.Visibility = hasGlyph && !isVolume ? Visibility.Visible : Visibility.Collapsed;
        GlanceNoticeIcon.FontSize = g.Kind == GlanceKind.Notice ? 19 : 16;
        GlanceNoticeIcon.Glyph = g.Kind switch
        {
            GlanceKind.Meeting => "\uE787",   // Calendar
            GlanceKind.Clipboard => "\uE77F", // Paste (clipboard)
            _ => _notifications.Current?.Glyph ?? "\uE7F4",
        };
        GlancePrayerIcon.Visibility = isPrayer ? Visibility.Visible : Visibility.Collapsed;
        GlanceNoticeLevel.Visibility = notice?.Level is not null && !isVolume ? Visibility.Visible : Visibility.Collapsed;
        GlanceNoticeLevel.Value = _notifications.Current?.Level ?? 0;
        GlanceDot.Fill = g.Kind == GlanceKind.Agent ? UiBrushes.ForStatus(g.Agent) : UiBrushes.Accent;
        if (awaiting) _pulse.Start(); else _pulse.Stop();

        GlanceBars.Visibility = isMusic ? Visibility.Visible : Visibility.Collapsed;
        if (isMusic && Media.IsPlaying) _beat.Start(); else _beat.Stop();

        if (isFocus)
        {
            var total = _pomodoro.Config.Duration(_pomodoro.State.Mode);
            double done = Math.Clamp(1 - _pomodoro.Remaining / total, 0, 1);
            GlanceRingProgress.StrokeDashArray = new DoubleCollection { done * RingUnits, 100 };
        }

        GlanceHost.Width = size.Width;
        GlanceHost.Height = size.Height;
        GlanceHost.Visibility = Visibility.Visible;

        if (size.Width != _shape.Width || size.Height != _shape.Height)
            ApplyShape(size, 14f);
        if (kindChanged)
        {
            _shownKind = g.Kind;
            NotchMotion.FadeIn(GlanceHost, riseDip: 0f, durationMs: 180);
        }
    }

    // ---------------------------------------------------------------- island shape

    private static SizeInt32 StageFor(SizeInt32 shape) => new(
        (int)Math.Ceiling(shape.Width + 2 * WindowPad),
        (int)Math.Ceiling(FloatGap + shape.Height + WindowPad));

    /// <summary>
    /// Morphs the island to a new shape. The window only ever hugs the shape plus its padding: it grows
    /// first (so the spring has room to run) and shrinks after the spring settles.
    /// </summary>
    private void ApplyShape(SizeInt32 shape, float radius)
    {
        _shape = shape;
        _peeking = false;

        var need = StageFor(shape);
        var grown = new SizeInt32(Math.Max(_windowDip.Width, need.Width), Math.Max(_windowDip.Height, need.Height));
        if (grown.Width != _windowDip.Width || grown.Height != _windowDip.Height)
        {
            _windowDip = grown;
            WindowHelper.Resize(this, grown);
        }

        _island.MorphTo(new Vector2(shape.Width, shape.Height), radius);

        _settleTimer.Stop();
        _settleTimer.Interval = ShapeSettleTime;
        _settleTimer.Start();
    }

    private void OnShapeSettled()
    {
        var need = StageFor(_shape);
        if (need.Width == _windowDip.Width && need.Height == _windowDip.Height) return;
        _windowDip = need;
        WindowHelper.Resize(this, need);
    }

    // The closed capsule swells a little when the pointer arrives, a beat before it opens (and relaxes if it leaves).
    private void Peek(bool on)
    {
        if (ViewModel.IsExpanded || _peeking == on) return;
        _peeking = on;
        var size = new Vector2(_shape.Width + (on ? PeekGrowX : 0), _shape.Height + (on ? PeekGrowY : 0));
        _island.MorphTo(size, 14f, damping: 0.6f, periodMs: 45);
    }

    // ---------------------------------------------------------------- open / close

    private void UpdateHeaderClock()
    {
        _headerClockTimer.Stop();
        if (!ViewModel.IsExpanded) return;
        var now = DateTimeOffset.Now;
        HeaderDate.Text = now.ToString("ddd, dd MMM");
        HeaderTime.Text = now.ToString("HH:mm");
        if (ViewModel.SelectedTab == NotchTab.Calendar) CalendarPage.RefreshToday();
        // Schedule the next visible minute boundary instead of ticking while closed.
        _headerClockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second)
            - TimeSpan.FromMilliseconds(now.Millisecond);
        _headerClockTimer.Start();
    }

    private void Expand(bool pin, NotchTab? tab = null)
    {
        CancelHover();
        if (pin) { _pinned = true; RevealForUser(); }

        // Contextual first page: whatever is asking for attention, else wherever you left off.
        tab ??= _glance.Kind switch
        {
            GlanceKind.Agent when _glance.Agent == AgentStatus.AwaitingApproval => NotchTab.Agents,
            GlanceKind.Clipboard => NotchTab.Dev,
            GlanceKind.Prayer => NotchTab.Prayer,
            GlanceKind.Meeting => NotchTab.Calendar,
            _ => null,
        };
        if (tab is { } t) SelectTab(t);
        if (ViewModel.IsExpanded) return;

        ViewModel.Toggle();
        SyncNotificationUi();
        _glanceTimer.Stop();
        UpdateHeaderClock();
        _beat.Stop();
        _eyes.Stop();
        _pulse.Stop();
        ExpandedPane.Visibility = Visibility.Visible;
        SyncTabs();

        // The capsule stretches into the card; the glance fades out as the page scales up inside it.
        NotchMotion.PopIn(ExpandedPane, ExpandedShape.Width / 2f, fromScale: 0.94f, delayMs: 70, durationMs: 300);
        NotchMotion.FadeOut(GlanceHost, 80, () =>
        {
            if (ViewModel.IsExpanded) GlanceHost.Visibility = Visibility.Collapsed;
        });

        _ = RefreshPortsAsync();
        _ = _prayer.RefreshIfStaleAsync();
        RefreshPalette();
        SyncProgressTimer();
    }

    private void Collapse()
    {
        CancelHover();
        _pinned = false;
        if (!ViewModel.IsExpanded) return;

        ViewModel.Toggle();
        _progressTimer.Stop();
        _headerClockTimer.Stop();
        _usageRefreshTimer.Stop();
        UpdateQuotaRefreshTimer();
        _prayerClockTimer.Stop();
        SystemUsagePage.Stop();
        NotchMotion.FadeOut(ExpandedPane, 90, () =>
        {
            if (!ViewModel.IsExpanded) ExpandedPane.Visibility = Visibility.Collapsed;
        });

        // Shrinks the shape back to whichever glance applies, then lets the glance pop back in.
        RefreshGlance();
        NotchMotion.PopIn(GlanceHost, _shape.Width / 2f, fromScale: 0.9f, delayMs: 110, durationMs: 260);
    }

    private void OpenPalette()
    {
        Expand(pin: true, NotchTab.Run);
        _ = NativeMethods.SetForegroundWindow(WindowHelper.GetHwnd(this));
    }


    private void OnTrayOpen(object? sender, EventArgs e)
    {
        if (ViewModel.IsExpanded && _pinned) Collapse();
        else Expand(pin: true);
    }

    private void OnTrayPalette(object? sender, EventArgs e) => OpenPalette();

    /// <summary>Thread-safe: a second launch of MyNotch asks this instance to open.</summary>
    internal void RequestOpen() => _dispatcher.TryEnqueue(() =>
    {
        Expand(pin: true);
        _ = NativeMethods.SetForegroundWindow(WindowHelper.GetHwnd(this));
    });

    private void OnTrayQuit(object? sender, EventArgs e) => Close();

    // ---------------------------------------------------------------- hover / pointer

    // The window is larger than the island (shadow room, float gap) and the XAML body is always full-size under
    // the clip, so XAML's own enter/exit events can't tell "over the island" from "next to it". Track that here.
    private bool ShapeContains(Windows.Foundation.Point p)
    {
        double left = (Pill.ActualWidth - _shape.Width) / 2.0;
        return p.X >= left - 6 && p.X <= left + _shape.Width + 6
            && p.Y >= -FloatGap && p.Y <= _shape.Height + 6;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        SetPointerInShape(ShapeContains(e.GetCurrentPoint(Pill).Position));

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e) => SetPointerInShape(false);

    private void SetPointerInShape(bool inside)
    {
        if (inside == _pointerInShape) return;
        _pointerInShape = inside;
        if (inside)
        {
            if (ViewModel.IsExpanded) CancelHover();
            else
            {
                ScheduleHover(HoverIntent.Open, HoverOpenDelay);
                Peek(true);
            }
        }
        else
        {
            if (ViewModel.IsExpanded && (!_pinned || !_isActive)) ScheduleHover(HoverIntent.Close, HoverCloseDelay);
            else CancelHover();
            Peek(false);
        }
    }

    // Any press on the island pins it: a click on the closed capsule opens it; a click inside the open one keeps it open.
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Pill);
        if (!point.Properties.IsLeftButtonPressed || !ShapeContains(point.Position)) return;
        if (ViewModel.IsExpanded) _pinned = true;
        else Expand(pin: true);
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ShapeContains(e.GetPosition(Pill))) return;
        e.Handled = true;
        CancelHover();
        _tray.ShowMenu();
        // The menu's modal loop swallows the button-up; make sure XAML isn't left holding the pointer.
        RootGrid.ReleasePointerCaptures();
    }

    // Scrolling over the page indicators flips through pages, stopping at the ends.
    private void TabBar_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(TabBar).Properties.MouseWheelDelta;
        if (delta == 0) return;
        int next = Math.Clamp((int)ViewModel.SelectedTab + (delta < 0 ? 1 : -1), 0, Enum.GetValues<NotchTab>().Length - 1);
        SelectTab((NotchTab)next);
        e.Handled = true;
    }

    private void ScheduleHover(HoverIntent intent, TimeSpan delay)
    {
        _hoverIntent = intent;
        _hoverTimer.Stop();
        _hoverTimer.Interval = delay;
        _hoverTimer.Start();
    }

    private void CancelHover()
    {
        _hoverIntent = HoverIntent.None;
        _hoverTimer.Stop();
    }

    private void OnHoverTick()
    {
        var intent = _hoverIntent;
        _hoverIntent = HoverIntent.None;
        if (intent == HoverIntent.Open && !ViewModel.IsExpanded) Expand(pin: false);
        else if (intent == HoverIntent.Close && ViewModel.IsExpanded && (!_pinned || !_isActive)) Collapse();
    }

    // ---------------------------------------------------------------- tabs

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton { Tag: string name } && Enum.TryParse<NotchTab>(name, out var tab))
        {
            ViewModel.SelectedTab = tab;
            SyncTabs();
        }
    }

    private void SelectTab(NotchTab tab)
    {
        var button = tab switch
        {
            NotchTab.Calendar => CalendarTab,
            NotchTab.System => SystemTab,
            NotchTab.Focus => FocusTab,
            NotchTab.Prayer => PrayerTab,
            NotchTab.Agents => AgentsTab,
            NotchTab.Dev => DevTab,
            NotchTab.Run => RunTab,
            NotchTab.Settings => SettingsTab,
            _ => MusicTab,
        };
        if (button.IsChecked == true) SyncTabs(); else button.IsChecked = true;
    }

    private void SyncTabs()
    {
        var tab = ViewModel.SelectedTab;
        var items = new (RadioButton Button, TextBlock Label, Viewbox Icon)[]
        {
            (MusicTab, MusicNavLabel, MusicNavIcon),
            (CalendarTab, CalendarNavLabel, CalendarNavIcon),
            (FocusTab, FocusNavLabel, FocusNavIcon),
            (PrayerTab, PrayerNavLabel, PrayerNavIcon),
            (AgentsTab, AgentsNavLabel, AgentsNavIcon),
            (SystemTab, SystemNavLabel, SystemNavIcon),
            (DevTab, DevNavLabel, DevNavIcon),
            (RunTab, RunNavLabel, RunNavIcon),
            (SettingsTab, SettingsNavLabel, SettingsNavIcon),
        };
        foreach (var item in items)
        {
            bool selected = item.Button.IsChecked == true;
            item.Label.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            item.Icon.Opacity = selected ? 1 : 0.55;
        }
        Show(MusicPage, tab == NotchTab.Music);
        Show(CalendarPage, tab == NotchTab.Calendar);
        if (tab == NotchTab.Calendar) CalendarPage.RefreshToday();
        Show(SystemUsagePage, tab == NotchTab.System);
        if (ViewModel.IsExpanded && tab == NotchTab.System) SystemUsagePage.Start(); else SystemUsagePage.Stop();
        Show(FocusPage, tab == NotchTab.Focus);
        Show(PrayerPage, tab == NotchTab.Prayer);
        _prayerClockTimer.Stop();
        if (ViewModel.IsExpanded && tab == NotchTab.Prayer) { UpdatePrayerPage(); _prayerClockTimer.Start(); }
        Show(AgentsHost, tab == NotchTab.Agents);
        _usageRefreshTimer.Stop();
        if (ViewModel.IsExpanded && tab == NotchTab.Agents) { _ = _usage.RefreshAsync(); _usageRefreshTimer.Start(); }
        UpdateQuotaRefreshTimer();
        Show(DevPageScroller, tab == NotchTab.Dev);
        if (ViewModel.IsExpanded && tab == NotchTab.Dev) _ = RefreshDevExtrasAsync();
        Show(RunPage, tab == NotchTab.Run);
        Show(SettingsPage, tab == NotchTab.Settings);
        if (tab == NotchTab.Settings) SettingsPage.Refresh();
        UpdateBadges();
        SyncProgressTimer();
        SyncExpandedSize();

        static void Show(UIElement page, bool visible)
        {
            bool was = page.Visibility == Visibility.Visible;
            page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && !was) NotchMotion.FadeIn(page, riseDip: 6f, durationMs: 200);
        }
    }

    private void SyncExpandedSize()
    {
        var shape = ViewModel.SelectedTab switch
        {
            NotchTab.Agents => new SizeInt32(600, _usagePanelHeight),
            NotchTab.Music => new SizeInt32(600, Media.HasMedia ? 204 : 180),
            NotchTab.Calendar => new SizeInt32(600, CalendarPage.HasMeeting ? 318 : 280),
            NotchTab.Dev => new SizeInt32(600, 280),
            NotchTab.Settings => new SizeInt32(600, 300),
            NotchTab.System => new SizeInt32(600, 324),
            NotchTab.Focus => new SizeInt32(600, 200),
            NotchTab.Prayer => new SizeInt32(600, 202),
            _ => ExpandedShape,
        };
        ExpandedPane.Width = shape.Width;
        ExpandedPane.Height = shape.Height;
        if (ViewModel.IsExpanded && (_shape.Width != shape.Width || _shape.Height != shape.Height))
            ApplyShape(shape, ExpandedRadius);
    }

    // Dots on tabs that have something to look at.
    private void UpdateBadges()
    {
        if (!_ready) return;
        bool awaiting = AgentAlert.ViewModel.Status == AgentStatus.AwaitingApproval;
        AgentsBadge.Visibility = awaiting && ViewModel.SelectedTab != NotchTab.Agents ? Visibility.Visible : Visibility.Collapsed;
        DevBadge.Visibility = DevTools.HasClipboardAction && ViewModel.SelectedTab != NotchTab.Dev ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- command palette

    private void RefreshPalette()
    {
        var query = string.Empty;
        int pages = Math.Max(1, (_commands.Commands.Count + 1) / 2);
        _actionPage = Math.Clamp(_actionPage, 0, pages - 1);
        Palette.SetMatches(_commands.Commands.Skip(_actionPage * 2).Take(2).ToArray(), query);
        PreviousActions.IsEnabled = _actionPage > 0;
        NextActions.IsEnabled = _actionPage + 1 < pages;
        ActionPageLabel.Text = $"{_actionPage + 1}/{pages}";
        PaletteList.SelectedIndex = Palette.SelectedIndex;
        Bindings.Update();
    }

    private void PreviousActions_Click(object sender, RoutedEventArgs e) { _actionPage--; RefreshPalette(); }
    private void NextActions_Click(object sender, RoutedEventArgs e) { _actionPage++; RefreshPalette(); }

    private void PaletteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedCommand cmd) ExecuteCommand(cmd);
    }

    private void ExecuteCommand(SavedCommand command)
    {
        if (_commands.TryExecute(command, out var error))
        {
            ViewModel.StatusText = $"Ran {command.Name}.";
            Collapse();
        }
        else
        {
            Palette.SetStatus($"Failed: {error}");
            Bindings.Update();
        }
    }

    // ---------------------------------------------------------------- shutdown

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Pending permission prompts fall back to Claude Code's own prompt instead of hanging.
        ResolveAllApprovals(ApprovalDecision.None);
        _webhook.ApprovalHandler = null;
        _displayWatcher.Dispose();
        _meetings.Dispose();
        _ciTimer.Stop();
        DisposeNotifications();
        DevTools.PropertyChanged -= OnDevToolsChanged;
        Media.PropertyChanged -= OnMediaChanged;
        _pomodoro.Changed -= OnPomodoroChanged;
        _pomodoro.PhaseCompleted -= OnPomodoroPhaseCompleted;
        _prayer.ScheduleChanged -= OnPrayerScheduleChanged;
        _prayer.ReminderDue -= OnPrayerReminderDue;
        _hoverTimer.Stop();
        _glanceTimer.Stop();
        _headerClockTimer.Stop();
        _lyricTimer.Stop();
        _prayerClockTimer.Stop();
        _progressTimer.Stop();
        _beat.Stop();
        _lyricsCts?.Cancel();
        _eyes.Stop();
        _lyricsCts?.Dispose();
        _pomodoro.Stop();
        _tray.Dispose();
        _media.Dispose();
        _lyricsService.Dispose();
        _prayer.Dispose();
        SystemUsagePage.Dispose();
        _usageRefreshTimer.Stop();
        _usage.Changed -= OnUsageChanged;
        _usage.Dispose();
        _clipboard.Dispose();
        _ = _webhook.StopAsync();
    }
}

