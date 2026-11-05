using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Models;

namespace MyNotch.Controls;

public sealed partial class UsageCardControl : UserControl
{
    private UsageLoad? _shown;
    private UsageProvider _provider;
    public event EventHandler? ResetRequested;
    private void ResetLimit_Click(object sender, RoutedEventArgs e) => ResetRequested?.Invoke(this, EventArgs.Empty);
    public void SetResetting(bool value) => ResetLimitButton.IsEnabled = !value;
    public UsageCardControl() { InitializeComponent(); }
    public void Apply(UsageProvider provider, UsageLoad load)
    {
        if (_shown == load && _provider == provider) return;
        _shown = load; _provider = provider;
        ServiceName.Text = UsagePresentation.Name(provider);
        CodexIcon.Visibility = provider == UsageProvider.Codex ? Visibility.Visible : Visibility.Collapsed;
        ClaudeIcon.Visibility = provider == UsageProvider.Claude ? Visibility.Visible : Visibility.Collapsed;
        AntigravityIcon.Visibility = provider == UsageProvider.Antigravity ? Visibility.Visible : Visibility.Collapsed;
        MuseIcon.Visibility = provider == UsageProvider.Muse ? Visibility.Visible : Visibility.Collapsed;
        var snapshot = load.Snapshot;
        PlanLabel.Text = snapshot?.Plan ?? "";
        bool canReset = provider == UsageProvider.Codex && load.Content is UsageContent.Ready && snapshot is { AvailableResets: > 0, ResetCredits.Count: > 0 };
        ResetLimitButton.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
        ResetLimitButton.Content = $"Reset limit ({snapshot?.AvailableResets ?? 0})";
        WindowsHost.Children.Clear();
        if (snapshot is not null)
            foreach (var window in snapshot.Windows)
            {
                var row = new StackPanel { Spacing = 2 };
                var header = new Grid { ColumnSpacing = 4 };
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = window.Label.Replace(" Models", "").Replace(" models", "").Replace("Claude and GPT", "Claude/GPT"), FontSize = 10, Foreground = Brush(235, 235, 235), TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTipService.SetToolTip(label, window.Label);
                var percent = new TextBlock { Text = window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%", FontSize = 11, Foreground = Brush(245, 245, 245) };
                var reset = new TextBlock { Text = window.ResetsAt is { } resetAt ? resetAt <= DateTimeOffset.Now ? "refresh due" : resetAt.LocalDateTime.ToString("ddd HH:mm") : "--", FontSize = 9, Foreground = Brush(145, 145, 145), VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(reset, UsagePresentation.Reset(window.ResetsAt, DateTimeOffset.Now));
                Grid.SetColumn(reset, 1); Grid.SetColumn(percent, 2);
                header.Children.Add(label); header.Children.Add(reset); header.Children.Add(percent);
                row.Children.Add(header);
                row.Children.Add(new ProgressBar { Maximum = 100, Value = window.UsedPercent, Height = 4, MinHeight = 4, CornerRadius = new CornerRadius(2), Background = Brush(58, 58, 58), Foreground = window.UsedPercent >= 90 ? Brush(255, 137, 108) : window.UsedPercent >= 75 ? Brush(255, 184, 66) : Brush(224, 224, 224) });
                WindowsHost.Children.Add(row);
            }
        StatusLabel.Text = load.Content switch
        {
            UsageContent.Waiting => "Open Agents to check usage",
            UsageContent.Loading => snapshot is null ? "Checking local account..." : "Refreshing - showing previous reading",
            UsageContent.Failed failed => (snapshot is null ? "" : "Previous reading - ") + UsagePresentation.Problem(provider, failed.Problem),
            UsageContent.Ready ready => $"Updated {ready.Snapshot.AsOf.LocalDateTime:HH:mm} - used",
            _ => throw new ArgumentOutOfRangeException(nameof(load)),
        };
        ToolTipService.SetToolTip(ServiceName, StatusLabel.Text);
        StatusLabel.Visibility = Visibility.Visible;
    }
    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Windows.UI.Color.FromArgb(255, red, green, blue));
}
