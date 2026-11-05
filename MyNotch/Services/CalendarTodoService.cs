using System.Text.Json;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Small local store. Commit by same-directory rename; failed writes keep the displayed state.</summary>
public sealed class CalendarTodoService
{
    private static readonly Lazy<CalendarTodoService> SharedStore = new(() => new());
    public static CalendarTodoService Shared => SharedStore.Value;
    public event EventHandler? Changed;
    private readonly string _path;
    private bool _canWrite = true;
    public IReadOnlyList<CalendarTask> Items { get; private set; } = Array.Empty<CalendarTask>();
    public string Status { get; private set; } = "";
    public CalendarTodoService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", "calendar-tasks.json");
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize(File.ReadAllText(_path), CalendarTodoJsonContext.Default.CalendarTodoDocument);
            if (document is not { Version: 1, Items: not null } || !CalendarTodo.Valid(document.Items)) throw new InvalidDataException();
            Items = document.Items;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _canWrite = false;
            Status = "Task file could not be loaded; existing file kept.";
        }
    }
    public bool Apply(TodoInput input)
    {
        if (!_canWrite) return false;
        var next = CalendarTodo.Step(Items, input);
        if (next.Problem != TodoProblem.None)
        {
            Status = next.Problem switch { TodoProblem.Empty => "Task title is empty.", TodoProblem.TooLong => "Keep tasks under 200 characters.", TodoProblem.Full => "Task storage is full.", _ => "Task could not be added." };
            return false;
        }
        if (ReferenceEquals(next.Items, Items)) return true;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var document = new CalendarTodoDocument(1, next.Items.ToArray());
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, CalendarTodoJsonContext.Default.CalendarTodoDocument));
            File.Move(temporary, _path, overwrite: true);
            Items = next.Items; Status = "";
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not save tasks. Please try again.";
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
