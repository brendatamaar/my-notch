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
    private MeetingService _meetings = null!;
    private bool _hiddenForFullscreen;
    private bool _fullscreenOverride; // the user explicitly opened the notch while an app was fullscreen

    private void InitializeFeatures()
    {
        _meetings = new MeetingService(_dispatcher);
        _meetings.Changed += (_, _) => OnMeetingsChanged();
        _meetings.MeetingSoon += (_, meeting) => _notifications.Post(new(
            "meeting:" + meeting.Start.UtcTicks, NotificationKind.Meeting,
            meeting.Start <= DateTimeOffset.Now ? "Meeting now" : $"Meeting in {Math.Max(1, (int)Math.Ceiling((meeting.Start - DateTimeOffset.Now).TotalMinutes))} min",
            meeting.Title, DateTimeOffset.Now, VisibleFor: TimeSpan.FromSeconds(20)));

        _tray.SettingsRequested += (_, _) => { Expand(pin: true, NotchTab.Settings); Activate(); };
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
        WindowHelper.ClipToStage(this, _windowDip, _stageDip);
        // A DPI change on the new monitor can resize the window after the move; settle once more.
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            WindowHelper.Resize(this, _windowDip);
            WindowHelper.ClipToStage(this, _windowDip, _stageDip);
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
