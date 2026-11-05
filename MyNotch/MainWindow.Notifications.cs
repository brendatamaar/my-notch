using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Models;
using MyNotch.Controls;
using MyNotch.Services;
using MyNotch.ViewModels;

namespace MyNotch;

public sealed partial class MainWindow
{
    private NotificationHub _notifications = null!;
    private ReminderNotificationService _reminders = null!;
    private WindowsNotificationService _windowsNotifications = null!;
    private readonly BluetoothNotificationService _bluetoothNotifications = new();
    private readonly BrightnessNotificationService _brightnessNotifications = new();
    private VolumeNotificationService _volumeNotifications = null!;
    private WellnessReminderService _wellness = null!;
    private readonly Dictionary<string, int> _quotaBands = new();
    private readonly List<string> _notificationFailures = new();
    private Flyout? _notificationFlyout;
    private int _notificationPage;
    private bool _showNotificationSettings;
    private bool _notificationsDisposed;

    private void InitializeNotifications()
    {
        _notifications = new NotificationHub(_dispatcher);
        _reminders = new ReminderNotificationService(_dispatcher, _notifications, CalendarTodoService.Shared);
        _windowsNotifications = new WindowsNotificationService(_notifications);
        _volumeNotifications = new VolumeNotificationService(_dispatcher);
        _wellness = new WellnessReminderService(_dispatcher, _notifications, () => _pomodoro.IsRunning);
        _notifications.Changed += (_, _) => SyncNotificationUi();
        _tray.NotificationSettingsRequested += OnNotificationSettingsRequested;
        _bluetoothNotifications.Changed += (name, change) => _notifications.Post(new("bluetooth:" + name, NotificationKind.Bluetooth, change switch
        {
            BluetoothChange.Added => "Bluetooth device added",
            BluetoothChange.Connected => "Bluetooth connected",
            _ => "Bluetooth disconnected",
        }, string.IsNullOrWhiteSpace(name) ? "Bluetooth device" : name, DateTimeOffset.Now));
        _volumeNotifications.Changed += (level, muted) => _notifications.Post(new("volume", NotificationKind.Volume, muted ? "Volume muted" : "Volume", muted ? "Muted" : $"{level}%", DateTimeOffset.Now, muted ? 0 : level));
        _brightnessNotifications.Changed += level => _notifications.Post(new("brightness", NotificationKind.Brightness, "Brightness", $"{level}%", DateTimeOffset.Now, level));
        _brightnessNotifications.ClockChanged += () => { TimeZoneInfo.ClearCachedData(); _reminders.ClockChanged(); _wellness.ClockChanged(); };
    }
    private void StartNotifications(nint hwnd)
    {
        TryStart("Bluetooth", _bluetoothNotifications.Start);
        TryStart("Volume", _volumeNotifications.Start);
        TryStart("Brightness", () => _brightnessNotifications.Start(hwnd));
        _reminders.Schedule();
        _wellness.Start();
        var windowsStart = _windowsNotifications.StartAsync();
#if DEBUG
        if (Environment.GetEnvironmentVariable("MYNOTCH_NOTIFICATION_PROBE") is { Length: > 0 } probePath) _ = WriteNotificationProbeAsync(windowsStart, probePath);
#endif
        UpdateQuotaRefreshTimer();
        if (_notifications.Enabled(NotificationKind.AiQuota)) _ = _usage.RefreshAsync();
        void TryStart(string name, Action start)
        {
            try { start(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            { _notificationFailures.Add(name + " monitoring unavailable (" + ex.HResult.ToString("X8") + ")"); }
        }
    }
    private void SyncNotificationUi()
    {
        if (_notificationsDisposed) return;
        var current = _notifications.Current;
        _noticeText = current is null ? null : current.Title + (current.Message.Length == 0 ? "" : "\n" + current.Message);
        _noticeUntil = _notifications.Until;
        RefreshGlance();
    }
    private void UpdateQuotaRefreshTimer()
    {
        if (_notifications is null || _notificationsDisposed) return;
        if (_notifications.Enabled(NotificationKind.AiQuota))
        { _usageRefreshTimer.Interval = ViewModel.IsExpanded && ViewModel.SelectedTab == NotchTab.Agents ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(5); _usageRefreshTimer.Start(); }
        else if (!ViewModel.IsExpanded || ViewModel.SelectedTab != NotchTab.Agents) _usageRefreshTimer.Stop();
    }
    private void CheckQuotaNotifications()
    {
        if (!_notifications.Enabled(NotificationKind.AiQuota)) return;
        var now = DateTimeOffset.Now;
        foreach (var provider in Enum.GetValues<UsageProvider>())
        {
            if (!_usage.IsEnabled(provider) || _usage.GetState(provider).Content is not UsageContent.Ready ready || now - ready.Snapshot.AsOf > TimeSpan.FromMinutes(20)) continue;
            foreach (var window in ready.Snapshot.Windows)
            {
                if (!double.IsFinite(window.UsedPercent) || window.ResetsAt <= now) continue;
                string key = $"{provider}:{window.Label}:{window.ResetsAt?.UtcTicks}";
                int previous = _quotaBands.GetValueOrDefault(key);
                var result = AlertThresholds.Quota(previous, window.UsedPercent);
                _quotaBands[key] = result.Band;
                if (!result.Notify) continue;
                int band = result.Band;
                _notifications.Post(new("quota:" + key, NotificationKind.AiQuota, band == 2 ? UsagePresentation.Name(provider) + " limit reached" : UsagePresentation.Name(provider) + " quota almost used", $"{window.Label}: {window.UsedPercent:0}% used" + (window.ResetsAt is { } reset ? $" - resets {reset.LocalDateTime:ddd HH:mm}" : ""), now));
            }
        }
        if (_quotaBands.Count > 256) foreach (string key in _quotaBands.Keys.Take(_quotaBands.Count - 128).ToArray()) _quotaBands.Remove(key);
    }
    private void OnNotificationSettingsRequested(object? sender, EventArgs e)
    {
        Expand(pin: true);
        Activate();
        _showNotificationSettings = true;
        _notificationFlyout ??= new Flyout();
        RenderNotificationFlyout();
        _dispatcher.TryEnqueue(() => _notificationFlyout.ShowAt(ExpandedPane));
    }
    private void RenderNotificationFlyout()
    {
        var panel = new StackPanel { Width = 330, Spacing = 10 };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var recent = SmallButton("Recent"); var settings = SmallButton("Settings");
        recent.Click += (_, _) => { _showNotificationSettings = false; RenderNotificationFlyout(); };
        settings.Click += (_, _) => { _showNotificationSettings = true; RenderNotificationFlyout(); };
        tabs.Children.Add(recent); tabs.Children.Add(settings); panel.Children.Add(tabs);
        if (_showNotificationSettings)
        {
            var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var kinds = Enum.GetValues<NotificationKind>();
            for (int i = 0; i < (kinds.Length + 1) / 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < kinds.Length; i++)
            {
                var kind = kinds[i];
                var toggle = new CheckBox { Content = NotificationName(kind), IsChecked = _notifications.Enabled(kind), FontSize = 12, MinHeight = 28 };
                toggle.Click += (_, _) =>
                {
                    if (!_notifications.SetEnabled(kind, toggle.IsChecked == true)) { toggle.IsChecked = _notifications.Enabled(kind); _notifications.Post(new("settings-save", NotificationKind.General, "Settings could not be saved", "Please try again.", DateTimeOffset.Now)); }
                    _reminders.Schedule(); UpdateQuotaRefreshTimer();
                    if (kind == NotificationKind.AiQuota && toggle.IsChecked == true) _ = _usage.RefreshAsync();
                };
                Grid.SetRow(toggle, i / 2); Grid.SetColumn(toggle, i % 2); grid.Children.Add(toggle);
            }
            panel.Children.Add(grid);
            panel.Children.Add(new TextBlock { Text = _windowsNotifications.Status, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = UiBrushes.Neutral });
            var enable = SmallButton("Enable Windows app notifications");
            enable.Click += async (_, _) => { enable.IsEnabled = false; Activate(); await _windowsNotifications.StartAsync(true); RenderNotificationFlyout(); };
            panel.Children.Add(enable);
            var setup = SmallButton("Set up Windows access");
            setup.Click += async (_, _) =>
            {
                setup.IsEnabled = false;
                string result = await SetupNotificationIdentityAsync();
                panel.Children.Add(new TextBlock { Text = result, TextWrapping = TextWrapping.Wrap, MaxLines = 5, FontSize = 11 });
                setup.IsEnabled = true;
            };
            panel.Children.Add(setup);
            var developerSettings = SmallButton("Open Windows Developer Mode settings");
            developerSettings.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));
            panel.Children.Add(developerSettings);
            var privacy = SmallButton("Windows notification privacy settings");
            privacy.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications"));
            panel.Children.Add(privacy);
            panel.Children.Add(new TextBlock { Text = "CPU/RAM: 90% for 30s. AI quota: 90% / 100%.\nBrightness depends on the display driver.\nMessage history stays in memory until MyNotch closes.", FontSize = 10, Foreground = UiBrushes.Neutral, TextWrapping = TextWrapping.Wrap });
            foreach (var failure in _notificationFailures) panel.Children.Add(new TextBlock { Text = failure, FontSize = 10, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            int pages = Math.Max(1, (_notifications.History.Count + 3) / 4);
            _notificationPage = Math.Clamp(_notificationPage, 0, pages - 1);
            foreach (var item in _notifications.History.Skip(_notificationPage * 4).Take(4))
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = $"{item.At.LocalDateTime:HH:mm}  {item.Title}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = item.Message, FontSize = 11, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = UiBrushes.Neutral });
                panel.Children.Add(row);
            }
            if (_notifications.History.Count == 0) panel.Children.Add(new TextBlock { Text = "No notifications yet", FontSize = 12, Foreground = UiBrushes.Neutral });
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var previous = SmallButton("\u2039"); var next = SmallButton("\u203a"); previous.IsEnabled = _notificationPage > 0; next.IsEnabled = _notificationPage + 1 < pages;
            previous.Click += (_, _) => { _notificationPage--; RenderNotificationFlyout(); }; next.Click += (_, _) => { _notificationPage++; RenderNotificationFlyout(); };
            pager.Children.Add(previous); pager.Children.Add(new TextBlock { Text = $"{_notificationPage + 1}/{pages}", VerticalAlignment = VerticalAlignment.Center }); pager.Children.Add(next); panel.Children.Add(pager);
        }
        _notificationFlyout!.Content = panel;
    }
    private static Button SmallButton(string text) => new() { Content = text, Style = (Style)Application.Current.Resources["NotchButtonStyle"], FontSize = 11, Padding = new Thickness(10, 4, 10, 4) };
    private static string NotificationName(NotificationKind kind) => kind switch
    {
        NotificationKind.General => "Focus / prayer", NotificationKind.Todo => "Task reminders", NotificationKind.AiQuota => "AI quota", NotificationKind.Resources => "CPU / RAM",
        NotificationKind.WindowsApp => "Windows apps", NotificationKind.Bluetooth => "Bluetooth", NotificationKind.Volume => "Volume", NotificationKind.Brightness => "Brightness",
        NotificationKind.Meeting => "Meetings", NotificationKind.Ci => "CI results",
        NotificationKind.Stretch => "Stretch reminder", NotificationKind.Water => "Drink water reminder", _ => kind.ToString(),
    };
    private static async Task<string> SetupNotificationIdentityAsync()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Setup-NotificationAccess.ps1");
        if (!File.Exists(script)) return "Setup script is missing. Rebuild MyNotch.";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-AppDirectory", AppContext.BaseDirectory }) start.ArgumentList.Add(arg);
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string message = (await output).Trim(); string errors = await error;
            return process.ExitCode == 0 ? message : errors.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) ? "Enable Developer Mode in Windows Settings, then try Set up again." : "Windows access registration failed. Check Windows package deployment policy and retry.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return "Windows access setup could not start."; }
    }
    private void DisposeNotifications()
    {
        _notificationsDisposed = true; _notificationFlyout?.Hide();
        _tray.NotificationSettingsRequested -= OnNotificationSettingsRequested;
        _windowsNotifications.Dispose(); _bluetoothNotifications.Dispose(); _volumeNotifications.Dispose(); _brightnessNotifications.Dispose(); _reminders.Dispose(); _wellness.Dispose(); _notifications.Dispose();
    }
#if DEBUG
    private async Task WriteNotificationProbeAsync(Task windowsStart, string path)
    {
        await windowsStart;
        _notificationFlyout ??= new Flyout();
        _showNotificationSettings = true; RenderNotificationFlyout();
        _showNotificationSettings = false; RenderNotificationFlyout();
        await File.WriteAllLinesAsync(path, new[] { "NotificationViews=True", "Volume=" + _volumeNotifications.IsRunning, "Brightness=" + _brightnessNotifications.IsRunning, "Bluetooth=" + _bluetoothNotifications.IsRunning, "Windows=" + _windowsNotifications.Status }.Concat(_notificationFailures));
    }
#endif
}
