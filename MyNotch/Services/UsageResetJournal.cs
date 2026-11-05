using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Persist the pending logical request before sending it. Unknown outcomes keep the same UUID.</summary>
public sealed class UsageResetJournal(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", "usage-resets");
    private string FileFor(string creditId) => Path.Combine(_directory, UsageReset.Key(creditId) + ".txt");
    public string GetOrCreate(string creditId)
    {
        Directory.CreateDirectory(_directory);
        string file = FileFor(creditId);
        if (File.Exists(file)) return Read(file);
        string key = Guid.NewGuid().ToString();
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(key);
            stream.Write(bytes); stream.Flush(flushToDisk: true);
            return key;
        }
        catch (IOException) when (File.Exists(file)) { return Read(file); }
    }
    public void Record(string creditId, UsageResetOutcome outcome)
    {
        // An explicitly non-consuming result ends this attempt; a later click is a new logical request.
        if (outcome is UsageResetOutcome.NothingToReset or UsageResetOutcome.NoCredit) File.Delete(FileFor(creditId));
    }
    private static string Read(string file) => Guid.TryParse(File.ReadAllText(file).Trim(), out var key) ? key.ToString() : throw new InvalidDataException("Reset request journal is unreadable");
}
