namespace MyNotch.Models;

public sealed record CalendarTask(Guid Id, DateOnly Date, string Text, bool Completed, TimeOnly? ReminderTime = null, bool ReminderSent = false);
public enum TodoProblem { None, Empty, TooLong, Duplicate, Full }
public abstract record TodoInput
{
    public sealed record Add(Guid Id, DateOnly Date, string Text) : TodoInput;
    public sealed record Complete(Guid Id, bool Value) : TodoInput;
    public sealed record Remove(Guid Id) : TodoInput;
    public sealed record Remind(Guid Id, TimeOnly? Time) : TodoInput;
    public sealed record ReminderDelivered(Guid Id, TimeOnly Time) : TodoInput;
}
public sealed record TodoTransition(IReadOnlyList<CalendarTask> Items, TodoProblem Problem = TodoProblem.None);
public static class CalendarTodo
{
    public const int MaxTextLength = 200;
    public const int MaxTasks = 5000;
    public static TodoTransition Step(IReadOnlyList<CalendarTask> items, TodoInput input) => input switch
    {
        TodoInput.Add add when string.IsNullOrWhiteSpace(add.Text) => new(items, TodoProblem.Empty),
        TodoInput.Add add when add.Text.Trim().Length > MaxTextLength => new(items, TodoProblem.TooLong),
        TodoInput.Add add when add.Id == Guid.Empty || items.Any(t => t.Id == add.Id) => new(items, TodoProblem.Duplicate),
        TodoInput.Add when items.Count >= MaxTasks => new(items, TodoProblem.Full),
        TodoInput.Add add => new(new[] { new CalendarTask(add.Id, add.Date, add.Text.Trim(), false) }.Concat(items).ToArray()),
        TodoInput.Complete complete when items.Any(t => t.Id == complete.Id && t.Completed != complete.Value) => new(items.Select(t => t.Id == complete.Id ? t with { Completed = complete.Value } : t).ToArray()),
        TodoInput.Complete => new(items),
        TodoInput.Remove remove when items.Any(t => t.Id == remove.Id) => new(items.Where(t => t.Id != remove.Id).ToArray()),
        TodoInput.Remove => new(items),
        TodoInput.Remind remind => new(items.Select(t => t.Id == remind.Id ? t with { ReminderTime = remind.Time, ReminderSent = false } : t).ToArray()),
        TodoInput.ReminderDelivered delivered => new(items.Select(t => t.Id == delivered.Id && t.ReminderTime == delivered.Time ? t with { ReminderSent = true } : t).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };
    public static bool Valid(IReadOnlyList<CalendarTask> items) => items.Count <= MaxTasks && items.All(t => t is not null && t.Id != Guid.Empty && !string.IsNullOrWhiteSpace(t.Text) && t.Text.Trim().Length <= MaxTextLength) && items.Select(t => t.Id).Distinct().Count() == items.Count;
}
