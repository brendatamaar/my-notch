using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyNotch.Interop;
using MyNotch.Models;
using MyNotch.Services;
using Windows.ApplicationModel.DataTransfer;

namespace MyNotch.Controls;

/// <summary>The Settings tab. Writes through <see cref="SettingsService"/>; MainWindow applies changes from its Changed event.</summary>
public sealed partial class SettingsControl : UserControl
{
    private readonly SettingsService _settings = SettingsService.Shared;
    private readonly List<Action> _refreshers = new();

    public event EventHandler? NotificationSettingsRequested;

    public SettingsControl()
    {
        InitializeComponent();
        AddStepper(FocusSteppers, "Focus", 1, 180, 5, s => s.FocusMinutes, (s, v) => s with { FocusMinutes = v });
        AddStepper(FocusSteppers, "Short break", 1, 60, 1, s => s.ShortBreakMinutes, (s, v) => s with { ShortBreakMinutes = v });
        AddStepper(FocusSteppers, "Long break", 1, 120, 5, s => s.LongBreakMinutes, (s, v) => s with { LongBreakMinutes = v });
        AddStepper(WellnessSteppers, "Stretch every", 10, 180, 5, s => s.StretchMinutes, (s, v) => s with { StretchMinutes = v });
        AddStepper(WellnessSteppers, "Drink water every", 10, 180, 5, s => s.WaterMinutes, (s, v) => s with { WaterMinutes = v });
        AddStepper(ApprovalWaitHost, "Wait before asking in terminal (s)", 10, 540, 10, s => s.ApprovalWaitSeconds, (s, v) => s with { ApprovalWaitSeconds = v });
        AddStepper(LeadHost, "Remind me before meetings (min)", 1, 60, 1, s => s.MeetingLeadMinutes, (s, v) => s with { MeetingLeadMinutes = v });
        _settings.Changed += (_, _) => Refresh();
        Refresh();
    }

    public void SetCalendarStatus(string status) => CalendarStatus.Text = status;

    /// <summary>Re-reads settings and the Run key (which Task Manager can change behind our back).</summary>
    public void Refresh()
    {
        var s = _settings.Current;
        var (startup, _) = StartupRegistration.Read();
        StartupBox.IsChecked = startup == StartupState.On;
        StartupBox.Content = StartupRegistration.Describe(startup);
        FullscreenBox.IsChecked = s.HideInFullscreen;
        ApprovalsBox.IsChecked = s.NotchApprovals;
        ClipsBox.IsChecked = s.ClipboardHistory;
        CiBox.IsChecked = s.WatchCi;
        if (FeedBox.FocusState == FocusState.Unfocused) FeedBox.Text = s.CalendarFeedUrl; // don't clobber typing
        DisplayButton.Content = DisplayLabel(s.Display);
        foreach (var refresh in _refreshers) refresh();
        SettingsStatus.Text = _settings.Status;
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_settings.Update(change)) SettingsStatus.Text = _settings.Status;
        Refresh();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        var (state, _) = StartupRegistration.Read();
        // Unchecked-but-registered states (another copy, disabled in Task Manager) mean "point it at this copy".
        bool enable = state != StartupState.On;
        if (!StartupRegistration.Set(enable, out var error)) SettingsStatus.Text = error;
        Refresh();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => Save(s => s with { HideInFullscreen = FullscreenBox.IsChecked == true });
    private void Approvals_Click(object sender, RoutedEventArgs e) => Save(s => s with { NotchApprovals = ApprovalsBox.IsChecked == true });
    private void Clips_Click(object sender, RoutedEventArgs e) => Save(s => s with { ClipboardHistory = ClipsBox.IsChecked == true });
    private void Ci_Click(object sender, RoutedEventArgs e) => Save(s => s with { WatchCi = CiBox.IsChecked == true });
    private void SaveFeed_Click(object sender, RoutedEventArgs e) => Save(s => s with { CalendarFeedUrl = FeedBox.Text.Trim() });
    private void Notifications_Click(object sender, RoutedEventArgs e) => NotificationSettingsRequested?.Invoke(this, EventArgs.Empty);

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch");
        try { Directory.CreateDirectory(folder); await Windows.System.Launcher.LaunchFolderPathAsync(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { SettingsStatus.Text = "Couldn't open " + folder; }
    }

    private void CopyHooks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(ClaudeHookSetup.SettingsJson);
            Clipboard.SetContent(package);
            SettingsStatus.Text = "Copied. Merge it into the \"hooks\" section of ~/.claude/settings.json, then restart Claude Code.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { SettingsStatus.Text = "Clipboard is busy; try again."; }
    }

    // ---------------------------------------------------------------- display picker

    private void DisplayMenu_Opening(object sender, object e)
    {
        DisplayMenu.Items.Clear();
        string current = _settings.Current.Display;
        void Add(string label, string value)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = current == value };
            item.Click += (_, _) => Save(s => s with { Display = value });
            DisplayMenu.Items.Add(item);
        }
        Add("Primary display", "primary");
        Add("Follow the active window", "follow");
        var displays = WindowHelper.Displays();
        if (displays.Count > 1)
        {
            DisplayMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (var d in displays) Add(d.Label, "display:" + d.Id.Value);
        }
    }

    private static string DisplayLabel(string value)
    {
        if (value == "follow") return "Follow the active window";
        if (value.StartsWith("display:", StringComparison.Ordinal) && ulong.TryParse(value[8..], out var id))
            return WindowHelper.Displays().FirstOrDefault(d => d.Id.Value == id).Label ?? "Display (disconnected)";
        return "Primary display";
    }

    // ---------------------------------------------------------------- steppers

    private void AddStepper(Panel host, string label, int min, int max, int step, Func<AppSettings, int> get, Func<AppSettings, int, AppSettings> set)
    {
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["NotchText2Brush"], TextTrimming = TextTrimming.CharacterEllipsis };
        var minus = StepButton("−", "Decrease " + label);
        var value = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var plus = StepButton("+", "Increase " + label);
        // Small steps near the minimum (1, 2, 3, 5, 10...) so 1-minute values stay reachable.
        int Next(int v, int dir) => Math.Clamp(dir > 0 ? (v < step ? v + 1 : v + step) : (v <= step ? v - 1 : v - step), min, max);
        minus.Click += (_, _) => Save(s => set(s, Next(get(s), -1)));
        plus.Click += (_, _) => Save(s => set(s, Next(get(s), +1)));
        Grid.SetColumn(minus, 1); Grid.SetColumn(value, 2); Grid.SetColumn(plus, 3);
        row.Children.Add(name); row.Children.Add(minus); row.Children.Add(value); row.Children.Add(plus);
        host.Children.Add(row);
        _refreshers.Add(() =>
        {
            int v = get(_settings.Current);
            value.Text = v.ToString();
            minus.IsEnabled = v > min; plus.IsEnabled = v < max;
        });
    }

    private static Button StepButton(string text, string name)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.Resources["NotchButtonStyle"], Padding = new Thickness(0), Width = 26, Height = 24, MinWidth = 0, MinHeight = 0, FontSize = 13, CornerRadius = new CornerRadius(8) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
}
