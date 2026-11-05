using System.Globalization;

namespace MyNotch.Models;

public readonly record struct CpuCounters(ulong Idle, ulong Kernel, ulong User);
public readonly record struct NetworkCounters(long Received, long Sent);
public sealed record SystemUsageSnapshot(double? CpuPercent, ulong? MemoryUsed, ulong? MemoryTotal,
    long? StorageUsed, long? StorageTotal, string Drive, double? DownloadRate, double? UploadRate,
    int Connections, TimeSpan Uptime)
{
    public string CpuName { get; init; } = "CPU";
    public ulong? CommitUsed { get; init; }
    public ulong? CommitTotal { get; init; }
    public double? GpuPercent { get; init; }
    public string GpuName { get; init; } = "GPU";
    public double? CpuSystemPercent { get; init; }
    public double? CpuUserPercent { get; init; }
    public IReadOnlyList<ProcessUsage> Processes { get; init; } = Array.Empty<ProcessUsage>();
    public int ProcessCount { get; init; }
    public IReadOnlyList<DriveUsage> Drives { get; init; } = Array.Empty<DriveUsage>();
    public IReadOnlyList<AdapterUsage> Adapters { get; init; } = Array.Empty<AdapterUsage>();
    public PowerUsage? Power { get; init; }
}
public sealed record ProcessUsage(string Name, double? CpuPercent, long MemoryBytes, int Id = 0, double? GpuPercent = null);
public sealed record DriveUsage(string Name, long Used, long Total);
public sealed record AdapterUsage(string Name, long LinkBitsPerSecond, string Address);
public sealed record PowerUsage(bool HasBattery, bool OnAc, bool Charging, int? BatteryPercent, TimeSpan? Remaining);
public enum SystemModule { Cpu, Gpu, Memory, Drives, Network, Power }

public static class SystemUsageMath
{
    public static double? CpuPercent(CpuCounters? previous, CpuCounters current)
    {
        if (previous is not { } before || current.Idle < before.Idle || current.Kernel < before.Kernel || current.User < before.User) return null;
        double total = (double)(current.Kernel - before.Kernel) + (current.User - before.User);
        double idle = current.Idle - before.Idle;
        return total > 0 && idle <= total ? 100 * (total - idle) / total : null;
    }
    public static (double? Download, double? Upload) NetworkRate(IReadOnlyDictionary<string, NetworkCounters>? previous, IReadOnlyDictionary<string, NetworkCounters> current, double seconds)
    {
        if (previous is null || !double.IsFinite(seconds) || seconds <= 0) return (null, null);
        double received = 0, sent = 0;
        foreach (var (id, now) in current)
        {
            if (!previous.TryGetValue(id, out var before)) continue;
            if (now.Received >= before.Received) received += (double)now.Received - before.Received;
            if (now.Sent >= before.Sent) sent += (double)now.Sent - before.Sent;
        }
        return (received / seconds, sent / seconds);
    }
    public static string Bytes(double? bytes)
    {
        if (bytes is null || !double.IsFinite(bytes.Value) || bytes < 0) return "--";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes.Value; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
