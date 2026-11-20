using Microsoft.UI;
using MyNotch.Interop;
using MyNotch.Models;
using MyNotch.Services;
using MyNotch.ViewModels;

namespace MyNotch;

/// <summary>Settings, Start with Windows, display placement, fullscreen hiding and the meeting countdown.</summary>
public sealed partial class MainWindow
{
    private readonly SettingsService _settings = SettingsService.Shared;
    private readonly DisplayWatcher _displayWatcher = new();
    private readonly UpdateService _updater = new();
    private MeetingService _meetings = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _ghostWatchTimer = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _updateStartTimer = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _updatePeriodicTimer = null!;
    private bool _hiddenForFullscreen;
    private bool _fullscreenOverride; // the user explicitly opened the notch while an app was fullscreen
    private enum GhostMode { None, Alt, MiddleClick, Manual }
    private GhostMode _ghostMode;
    private DateTimeOffset _ghostEnteredAt;
    private string? _announcedUpdateTag;

    private void InitializeFeatures()
    {
        _meetings = new MeetingService(_dispatcher);
        _meetings.Changed += (_, _) => OnMeetingsChanged();
        _meetings.MeetingSoon += (_, meeting) => _notifications.Post(new(
            "meeting:" + meeting.Start.UtcTicks, NotificationKind.Meeting,
            meeting.Start <= DateTimeOffset.Now ? "Meeting now" : $"Meeting in {Math.Max(1, (int)Math.Ceiling((meeting.Start - DateTimeOffset.Now).TotalMinutes))} min",
            meeting.Title, DateTimeOffset.Now, VisibleFor: TimeSpan.FromSeconds(20)));

        _ghostWatchTimer = _dispatcher.CreateTimer();
        _ghostWatchTimer.Interval = TimeSpan.FromMilliseconds(90);
        _ghostWatchTimer.IsRepeating = true;
        _ghostWatchTimer.Tick += (_, _) => OnGhostWatchTick();

        _updateStartTimer = CreateOneShot(() => _ = _updater.CheckAsync(force: false));
        _updateStartTimer.Interval = TimeSpan.FromSeconds(15);
        _updatePeriodicTimer = _dispatcher.CreateTimer();
        _updatePeriodicTimer.Interval = TimeSpan.FromHours(6);
        _updatePeriodicTimer.IsRepeating = true;
        _updatePeriodicTimer.Tick += (_, _) => _ = _updater.CheckAsync(force: false);

        _updater.Changed += (_, _) => _dispatcher.TryEnqueue(OnUpdateStateChanged);

        _tray.SettingsRequested += (_, _) => { Expand(pin: true, NotchTab.Settings); Activate(); };
        _tray.ClickThroughChecked = () => _ghostMode == GhostMode.Manual;
        _tray.ClickThroughToggleRequested += (_, _) => ToggleManualClickThrough();
        _tray.UpdateItemLabel = () => _updater.State is { Kind: UpdateStatusKind.Available, Release: { } r } ? $"Update to {r.Tag}" : null;
        _tray.UpdateRequested += (_, _) => StartInNotchUpdate();
        _tray.StartupItem = () =>
        {
            var (state, _) = StartupRegistration.Read();
            return (StartupRegistration.Describe(state), state == StartupState.On);
        };
        _tray.StartupToggleRequested += (_, _) =>
        {
            var (state, _) = StartupRegistration.Read();
            if (!StartupRegistration.Set(state != StartupState.On, out var error)) ShowNotice(error, TimeSpan.FromSeconds(6));
            SettingsPage.Refresh();
        };
        SettingsPage.NotificationSettingsRequested += OnNotificationSettingsRequested;
        SettingsPage.CheckUpdateRequested += (_, _) => _ = _updater.CheckAsync(force: true);
        SettingsPage.InstallUpdateRequested += (_, _) => StartInNotchUpdate();
        SettingsPage.ApplyUpdateState(_updater.State);

        _settings.Changed += (_, _) => ApplySettings();
        _displayWatcher.FullscreenChanged += (_, full) => OnFullscreenChanged(full);
        _displayWatcher.ForegroundChanged += (_, hwnd) => OnForegroundChanged(hwnd);
        _displayWatcher.ActivationChanged += (_, _) => { if (!_hiddenForFullscreen) WindowHelper.KeepOnTop(this); };
        ApplySettings();
    }

    private void StartFeatures(IntPtr hwnd)
    {
        _displayWatcher.Start(hwnd);
        PlaceOnConfiguredDisplay();
        var s = _settings.Current;
        _meetings.Configure(s.CalendarFeedUrl, TimeSpan.FromMinutes(s.MeetingLeadMinutes));
        _updateStartTimer.Start();
        _updatePeriodicTimer.Start();
    }

    private void DisposeFeatures()
    {
        _ghostWatchTimer.Stop();
        _updateStartTimer.Stop();
        _updatePeriodicTimer.Stop();
        _updater.Dispose();
    }

    // ---------------------------------------------------------------- click-through / ghost mode

    private void ApplyCurrentClip()
    {
        if (ViewModel.IsExpanded || _peeking)
            WindowHelper.ClipToStage(this, _windowDip, _stageDip);
        else
            WindowHelper.ClipToCollapsedPill(this, _windowDip, _shape);
    }

    private void EnterGhost(GhostMode mode)
    {
        if (mode == GhostMode.None) { ExitGhost(); return; }
        CancelHover();
        Peek(false);
        if (ViewModel.IsExpanded) Collapse();
        _pointerInShape = false;
        _ghostMode = mode;
        _ghostEnteredAt = DateTimeOffset.UtcNow;
        RootGrid.Opacity = 0.18;
        WindowHelper.SetClickThrough(this, true);
        if (mode is GhostMode.Alt or GhostMode.MiddleClick) _ghostWatchTimer.Start();
        else _ghostWatchTimer.Stop();
    }

    private void ExitGhost()
    {
        if (_ghostMode == GhostMode.None) return;
        _ghostMode = GhostMode.None;
        _ghostWatchTimer.Stop();
        RootGrid.Opacity = 1.0;
        WindowHelper.SetClickThrough(this, false);
        ApplyCurrentClip();
    }

    private void ToggleManualClickThrough()
    {
        if (_ghostMode == GhostMode.Manual) ExitGhost();
        else EnterGhost(GhostMode.Manual);
    }

    private void OnGhostWatchTick()
    {
        if (_ghostMode == GhostMode.Alt)
        {
            if (!WindowHelper.IsAltHeld()) ExitGhost();
        }
        else if (_ghostMode == GhostMode.MiddleClick)
        {
            // Keep ghosted briefly after middle-click and while the cursor remains over/near the pill.
            if (DateTimeOffset.UtcNow - _ghostEnteredAt < TimeSpan.FromMilliseconds(450)) return;
            if (!WindowHelper.IsCursorNearPill(this, _shape, marginDip: 24)) ExitGhost();
        }
        else
        {
            _ghostWatchTimer.Stop();
        }
    }

    // ---------------------------------------------------------------- updates

    private void OnUpdateStateChanged()
    {
        var state = _updater.State;
        SettingsPage.ApplyUpdateState(state);
        UpdateBadges();
        if (state.Kind == UpdateStatusKind.Available && state.Release is { } release
            && !string.Equals(_announcedUpdateTag, release.Tag, StringComparison.OrdinalIgnoreCase))
        {
            _announcedUpdateTag = release.Tag;
            ShowNotice($"Update {release.Tag} available\nOpen Settings to install", TimeSpan.FromSeconds(10));
        }
    }

    private void StartInNotchUpdate()
    {
        Expand(pin: true, NotchTab.Settings);
        Activate();
        _ = _updater.InstallAndExitAsync(() => _dispatcher.TryEnqueue(Close));
    }

    /// <summary>Applies every setting that has a live effect. Safe to call repeatedly.</summary>
    private void ApplySettings()
    {
        var s = _settings.Current;
        _pomodoro.Configure(s.FocusConfig);
        foreach (var provider in Enum.GetValues<UsageProvider>())
            _usage.SetEnabled(provider, !s.DisabledUsageProviders.Contains(provider.ToString()));
        SyncUsageServicesMenu();
        if (!s.ClipboardHistory) DevTools.ClearClips();
        if (_servicesStarted)
        {
            _meetings.Configure(s.CalendarFeedUrl, TimeSpan.FromMinutes(s.MeetingLeadMinutes));
            PlaceOnConfiguredDisplay();
            OnFullscreenChanged(_displayWatcher.IsFullscreen);
        }
        if (!s.NotchApprovals) ResolveAllApprovals(ApprovalDecision.None);
    }

    // ---------------------------------------------------------------- display placement

    private void PlaceOnConfiguredDisplay()
    {
        string mode = _settings.Current.Display;
        DisplayId? target = null;
        if (mode.StartsWith("display:", StringComparison.Ordinal) && ulong.TryParse(mode[8..], out var id))
            target = WindowHelper.Displays().Any(d => d.Id.Value == id) ? new DisplayId(id) : null; // unplugged: fall back to primary
        else if (mode == "follow")
            target = WindowHelper.TargetDisplay ?? WindowHelper.DisplayOf(NativeMethods.GetForegroundWindow());
        if (mode == "primary" || target is null)
        {
            try { target = Microsoft.UI.Windowing.DisplayArea.Primary.DisplayId; } catch (System.Runtime.InteropServices.COMException) { }
        }
        MoveToDisplay(target);
    }

    private void MoveToDisplay(DisplayId? target)
    {
        if (target is null || (WindowHelper.TargetDisplay is { } current && current.Value == target.Value.Value)) return;
        WindowHelper.TargetDisplay = target;
        WindowHelper.Resize(this, _windowDip);
        ApplyCurrentClip();
        // A DPI change on the new monitor can resize the window after the move; settle once more.
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            WindowHelper.Resize(this, _windowDip);
            ApplyCurrentClip();
        });
        _displayWatcher.Evaluate();
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        // Follow mode moves only while closed, so an open panel never jumps away from the pointer.
        if (_settings.Current.Display != "follow" || ViewModel.IsExpanded) return;
        MoveToDisplay(WindowHelper.DisplayOf(hwnd));
    }

    // ---------------------------------------------------------------- fullscreen

    private void OnFullscreenChanged(bool full)
    {
        if (!full) _fullscreenOverride = false;
        bool hide = full && _settings.Current.HideInFullscreen && !_fullscreenOverride;
        if (hide == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hide;
        if (hide)
        {
            Collapse();
            // Nothing is visible: stop the looping glance animations (the music bars would otherwise keep the
            // compositor busy for the whole fullscreen video).
            _beat.Stop(); _eyes.Stop(); _pulse.Stop();
            WindowHelper.Hide(this);
        }
        else
        {
            WindowHelper.ShowWithoutActivating(this);
            RefreshGlance(); // restarts whichever animation the current glance uses
        }
    }

    /// <summary>An explicit open (tray, second launch, hotkey) wins over fullscreen hiding until the fullscreen app ends.</summary>
    private void RevealForUser()
    {
        if (!_hiddenForFullscreen) return;
        _fullscreenOverride = true;
        _hiddenForFullscreen = false;
        WindowHelper.ShowWithoutActivating(this);
    }

    // ---------------------------------------------------------------- meetings

    private void OnMeetingsChanged()
    {
        SettingsPage.SetCalendarStatus(_meetings.Status);
        bool hadMeeting = CalendarPage.HasMeeting;
        CalendarPage.SetMeeting(_meetings.Next, DateTimeOffset.Now);
        if (hadMeeting != CalendarPage.HasMeeting && ViewModel.SelectedTab == NotchTab.Calendar) SyncExpandedSize();
        RefreshGlance();
    }

    // ---------------------------------------------------------------- usage services menu (persisted)

    private readonly Dictionary<UsageProvider, Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem> _serviceItems = new();

    private void SyncUsageServicesMenu()
    {
        foreach (var (provider, item) in _serviceItems) item.IsChecked = _usage.IsEnabled(provider);
        if (_ready) SyncUsageCards();
    }

    private void SetUsageProviderEnabled(UsageProvider provider, bool enabled)
    {
        _settings.Update(s => s with
        {
            DisabledUsageProviders = enabled
                ? s.DisabledUsageProviders.Where(p => p != provider.ToString()).ToArray()
                : s.DisabledUsageProviders.Append(provider.ToString()).Distinct().ToArray(),
        });
        _usage.SetEnabled(provider, enabled);
        if (enabled) _ = _usage.RefreshAsync();
    }
}
