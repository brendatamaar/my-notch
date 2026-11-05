using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Models;
using MyNotch.Services;
using Microsoft.UI.Xaml.Input;

namespace MyNotch.Controls;

public sealed partial class CalendarControl : UserControl
{
    private DateOnly _selected = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly _month;
    private DateOnly _today;
    private readonly CalendarTodoService _todos = CalendarTodoService.Shared;
    private int _taskPage;
    private const int TasksPerPage = 2;
    public CalendarControl()
    {
        InitializeComponent();
        _today = _selected; _month = CalendarMonth.First(_selected);
        Loaded += (_, _) => { _todos.Changed += StoreChanged; Render(); };
        Unloaded += (_, _) => _todos.Changed -= StoreChanged;
        for (int i = 0; i < 7; i++) DaysGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 7; i++) DaysGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        Render();
    }
    private void StoreChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Render);

    private string _joinUrl = "";
    public bool HasMeeting => MeetingRow.Visibility == Visibility.Visible;

    /// <summary>Shows the next meeting (from the calendar feed) above the task list; null hides the row.</summary>
    public void SetMeeting(Meeting? meeting, DateTimeOffset now)
    {
        MeetingRow.Visibility = meeting is null ? Visibility.Collapsed : Visibility.Visible;
        if (meeting is null) return;
        MeetingTitle.Text = meeting.Title;
        ToolTipService.SetToolTip(MeetingTitle, string.IsNullOrWhiteSpace(meeting.Location) ? meeting.Title : $"{meeting.Title}\n{meeting.Location}");
        var start = meeting.Start.ToLocalTime();
        var left = meeting.Start - now;
        string relative = left <= TimeSpan.Zero ? "now" : left.TotalMinutes < 60 ? $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min"
            : start.Date == now.LocalDateTime.Date ? $"in {(int)left.TotalHours} h {left.Minutes} min" : start.Date == now.LocalDateTime.Date.AddDays(1) ? "tomorrow" : start.ToString("ddd", CultureInfo.InvariantCulture);
        MeetingWhen.Text = $"{start:HH:mm}–{meeting.End.ToLocalTime():HH:mm} · {relative}";
        _joinUrl = meeting.JoinUrl;
        MeetingJoin.Visibility = meeting.HasJoin ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MeetingJoin_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_joinUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) await Windows.System.Launcher.LaunchUriAsync(uri);
    }
    public void RefreshToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_today == today) return;
        if (_selected == _today) { _selected = today; _month = CalendarMonth.First(today); }
        _today = today; Render();
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { _month = CalendarMonth.Move(_month, -1); Render(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _month = CalendarMonth.Move(_month, 1); Render(); }
    private void Today_Click(object sender, RoutedEventArgs e) { _taskPage = 0; _today = _selected = DateOnly.FromDateTime(DateTime.Now); _month = CalendarMonth.First(_selected); Render(); }
    private void Render()
    {
        MonthHeading.Text = _month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        SelectedWeekday.Text = _selected.ToString("dddd", CultureInfo.InvariantCulture);
        SelectedDay.Text = _selected.Day.ToString();
        SelectedMonth.Text = _selected.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        SelectedDetail.Text = $"Week {ISOWeek.GetWeekOfYear(_selected.ToDateTime(TimeOnly.MinValue))} - day {_selected.DayOfYear}";
        PreviousMonth.IsEnabled = _month > DateOnly.MinValue;
        NextMonth.IsEnabled = _month < new DateOnly(9999, 12, 1);
        DaysGrid.Children.Clear();
        string[] weekdays = ["M", "T", "W", "T", "F", "S", "S"];
        for (int i = 0; i < 7; i++)
        {
            var label = new TextBlock { Text = weekdays[i], FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(125, 128, 138) };
            Grid.SetColumn(label, i); DaysGrid.Children.Add(label);
        }
        var cells = CalendarMonth.Cells(_month);
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] is not { } date) continue;
            bool selected = date == _selected, today = date == _today, current = date.Month == _month.Month && date.Year == _month.Year;
            var button = new Button
            {
                Content = date.Day.ToString(), Style = (Style)Application.Current.Resources["NotchButtonStyle"],
                MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(0), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(6),
                Background = selected ? Brush(72, 140, 235) : today ? Brush(28, 44, 65) : Brush(0, 0, 0),
                Foreground = selected || current ? Brush(242, 242, 246) : Brush(86, 89, 99),
                BorderThickness = today && !selected ? new Thickness(1) : new Thickness(0), BorderBrush = Brush(105, 173, 255),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, date.ToString("D", CultureInfo.InvariantCulture) + (today ? ", today" : ""));
            var tasks = _todos.Items.Where(t => t.Date == date).ToArray();
            if (tasks.Length > 0)
            {
                var content = new Grid();
                content.Children.Add(new TextBlock { Text = date.Day.ToString(), FontSize = 11, Foreground = button.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 3, Height = 3, Fill = selected ? Brush(255, 255, 255) : tasks.Any(t => !t.Completed) ? Brush(105, 173, 255) : Brush(110, 113, 123), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center });
                button.Content = content;
                ToolTipService.SetToolTip(button, $"{tasks.Count(t => !t.Completed)} pending / {tasks.Length} tasks");
            }
            button.Click += (_, _) => { _taskPage = 0; _selected = date; _month = CalendarMonth.First(date); Render(); };
            Grid.SetColumn(button, i % 7); Grid.SetRow(button, 1 + i / 7); DaysGrid.Children.Add(button);
        }
        RenderTasks();
    }
    private void AddTask_Click(object sender, RoutedEventArgs e) => AddTask();
    private void AddTask()
    {
        if (_todos.Apply(new TodoInput.Add(Guid.NewGuid(), _selected, TaskInput.Text))) { TaskInput.Text = ""; _taskPage = 0; }
        Render();
    }
    private void PreviousTasks_Click(object sender, RoutedEventArgs e) { _taskPage--; RenderTasks(); }
    private void NextTasks_Click(object sender, RoutedEventArgs e) { _taskPage++; RenderTasks(); }
    private void RenderTasks()
    {
        var tasks = _todos.Items.Where(t => t.Date == _selected).ToArray();
        int pages = Math.Max(1, (tasks.Length + TasksPerPage - 1) / TasksPerPage);
        _taskPage = Math.Clamp(_taskPage, 0, pages - 1);
        TodoCount.Text = $"{tasks.Count(t => t.Completed)}/{tasks.Length} done";
        TasksHost.Children.Clear();
        if (tasks.Length == 0) TasksHost.Children.Add(new TextBlock { Text = "No tasks for this date", FontSize = 11, Margin = new Thickness(0, 5, 0, 5), Foreground = Brush(125, 128, 138) });
        foreach (var task in tasks.Skip(_taskPage * TasksPerPage).Take(TasksPerPage))
        {
            var row = new Grid { Height = 25, ColumnSpacing = 5 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(21) });
            var check = new CheckBox { IsChecked = task.Completed, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "Complete " + task.Text);
            check.Click += (_, _) => { _todos.Apply(new TodoInput.Complete(task.Id, check.IsChecked == true)); Render(); };
            row.Children.Add(check);
            var label = new TextBlock { Text = task.Text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center,
                Foreground = task.Completed ? Brush(120, 123, 133) : Brush(235, 235, 240), TextDecorations = task.Completed ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None };
            ToolTipService.SetToolTip(label, task.Text); Grid.SetColumn(label, 1); row.Children.Add(label);
            var remove = new Button { Content = "\u00d7", Style = (Style)Application.Current.Resources["NotchButtonStyle"], Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, Height = 22, Width = 21, FontSize = 14 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Delete " + task.Text);
            remove.Click += (_, _) => { _todos.Apply(new TodoInput.Remove(task.Id)); Render(); };
            var reminder = new Button { Content = task.ReminderTime is { } at ? at.ToString("HH:mm") : "\uE787", FontFamily = new FontFamily(task.ReminderTime is null ? "Segoe Fluent Icons" : "Segoe UI"), FontSize = 10, Style = (Style)Application.Current.Resources["NotchButtonStyle"], Padding = new Thickness(2, 0, 2, 0), MinWidth = 0, MinHeight = 0, Height = 22, IsEnabled = !task.Completed };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reminder, "Reminder for " + task.Text);
            ToolTipService.SetToolTip(reminder, task.ReminderSent ? "Reminder delivered; click to reschedule" : "Set reminder time");
            var settings = new StackPanel { Spacing = 8, Width = 230 };
            settings.Children.Add(new TextBlock { Text = "Reminder - " + task.Date.ToString("dd MMM"), FontSize = 13 });
            var enabled = new ToggleSwitch { Header = "Remind me", IsOn = task.ReminderTime is not null };
            var picker = new TimePicker { ClockIdentifier = "24HourClock", Time = (task.ReminderTime ?? TimeOnly.FromDateTime(DateTime.Now.AddMinutes(5))).ToTimeSpan(), HorizontalAlignment = HorizontalAlignment.Stretch };
            var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Stretch };
            settings.Children.Add(enabled); settings.Children.Add(picker); settings.Children.Add(save);
            var flyout = new Flyout { Content = settings }; reminder.Flyout = flyout;
            save.Click += (_, _) => { _todos.Apply(new TodoInput.Remind(task.Id, enabled.IsOn ? TimeOnly.FromTimeSpan(picker.Time) : null)); flyout.Hide(); Render(); };
            Grid.SetColumn(reminder, 2); row.Children.Add(reminder);
            Grid.SetColumn(remove, 3); row.Children.Add(remove);
            TasksHost.Children.Add(row);
        }
        TaskPager.Visibility = tasks.Length > TasksPerPage ? Visibility.Visible : Visibility.Collapsed;
        TaskPageLabel.Text = $"{_taskPage + 1} / {pages}";
        PreviousTasks.IsEnabled = _taskPage > 0; NextTasks.IsEnabled = _taskPage + 1 < pages;
        TaskStatus.Text = _todos.Status;
        ToolTipService.SetToolTip(TaskStatus, _todos.Status);
        TaskStatus.Visibility = string.IsNullOrEmpty(_todos.Status) ? Visibility.Collapsed : Visibility.Visible;
    }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
}
