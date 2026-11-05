namespace MyNotch.Models;

public static class CalendarMonth
{
    public static DateOnly First(DateOnly date) => new(date.Year, date.Month, 1);
    public static DateOnly Move(DateOnly month, int direction)
    {
        int index = (int)Math.Clamp((long)(month.Year - 1) * 12 + month.Month - 1 + direction, 0, 9999 * 12 - 1);
        return new(index / 12 + 1, index % 12 + 1, 1);
    }
    /// <summary>Monday-first, fixed six-week grid. Out-of-range boundary cells stay empty.</summary>
    public static IReadOnlyList<DateOnly?> Cells(DateOnly month)
    {
        var first = First(month);
        int start = first.DayNumber - ((int)first.DayOfWeek + 6) % 7;
        return Enumerable.Range(0, 42).Select(i => start + i is >= 0 and <= 3652058 ? (DateOnly?)DateOnly.FromDayNumber(start + i) : null).ToArray();
    }
}
