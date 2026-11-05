using System.Text.Json;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Loads and atomically saves <see cref="AppSettings"/>. UI-thread only. A corrupt file is kept and never overwritten.</summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> SharedInstance = new(() => new());
    public static SettingsService Shared => SharedInstance.Value;

    private readonly string _path;
    private bool _canWrite = true;

    public AppSettings Current { get; private set; } = new();
    public string Status { get; private set; } = "";
    public event EventHandler? Changed;

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", "settings.json");
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 64 * 1024) throw new InvalidDataException();
            Current = (JsonSerializer.Deserialize(File.ReadAllText(_path), AppSettingsJsonContext.Default.AppSettings) ?? new()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _canWrite = false;
            Status = "settings.json could not be read; defaults are in use and the file is kept.";
        }
    }

    /// <summary>Applies a change and persists it. Returns false (and keeps the old settings) when saving fails.</summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current).Normalize();
        if (next.SameAs(Current)) return true;
        if (!_canWrite) { Status = "Settings are read-only because settings.json could not be read."; return false; }
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, AppSettingsJsonContext.Default.AppSettings));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Settings could not be saved.";
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
        Current = next;
        Status = "";
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
