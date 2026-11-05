using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using MyNotch.Interop;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>Single background sampler; no process handles, addresses or credentials leave this machine.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SystemUsageReader : IDisposable
{
    private readonly object _gate = new();
    private readonly GpuUsageCounter _gpu = new();
    private CpuCounters? _cpu;
    private Dictionary<string, NetworkCounters>? _network;
    private Dictionary<int, (double Cpu, long Started)> _processTimes = new();
    private IReadOnlyList<ProcessUsage> _processes = Array.Empty<ProcessUsage>();
    private IReadOnlyList<DriveUsage> _drives = Array.Empty<DriveUsage>();
    private long _stamp, _processStamp, _driveStamp;
    private int _processCount;
    private bool _disposed;
    private string? _gpuName;
    private string _cpuName = "CPU";
    public SystemUsageSnapshot? Capture(bool reset)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (reset)
            {
                _cpu = null; _network = null; _processTimes.Clear();
                _processStamp = _driveStamp = 0;
                _gpu.Start(); _gpuName ??= ReadGpuName();
                using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                _cpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
            }
            long stamp = Stopwatch.GetTimestamp();
            double seconds = _stamp == 0 ? 0 : (stamp - _stamp) / (double)Stopwatch.Frequency;
            double? cpu = null, system = null, userPercent = null;
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var current = new CpuCounters(idle.Ticks, kernel.Ticks, user.Ticks);
                cpu = SystemUsageMath.CpuPercent(_cpu, current);
                if (cpu is not null && _cpu is { } before)
                {
                    double total = (double)(current.Kernel - before.Kernel) + (current.User - before.User);
                    userPercent = 100d * (current.User - before.User) / total;
                    system = Math.Max(0, cpu.Value - userPercent.Value);
                }
                _cpu = current;
            }
            else _cpu = null;
            var memory = new NativeMethods.MemoryStatusEx { Length = 64 };
            bool hasMemory = NativeMethods.GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 && memory.AvailablePhysical <= memory.TotalPhysical;
            var networks = new Dictionary<string, NetworkCounters>();
            var adapters = new List<AdapterUsage>();
            bool hasNetwork = true;
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    try
                    {
                        var stats = adapter.GetIPStatistics(); networks[adapter.Id] = new(stats.BytesReceived, stats.BytesSent);
                        string address = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                        adapters.Add(new(adapter.Name, adapter.Speed, address));
                    }
                    catch (NetworkInformationException) { hasNetwork = false; }
                }
            }
            catch (NetworkInformationException) { hasNetwork = false; }
            var rate = hasNetwork ? SystemUsageMath.NetworkRate(_network, networks, seconds) : (Download: (double?)null, Upload: (double?)null);
            _network = hasNetwork ? networks : null; _stamp = stamp;
            if (_driveStamp == 0 || Stopwatch.GetElapsedTime(_driveStamp, stamp).TotalSeconds >= 30) ReadDrives(stamp);
            if (_processStamp == 0 || Stopwatch.GetElapsedTime(_processStamp, stamp).TotalSeconds >= 5) ReadProcesses(stamp);
            PowerUsage? power = null;
            if (NativeMethods.GetSystemPowerStatus(out var ps))
                power = new((ps.BatteryFlag & 128) == 0 && ps.BatteryFlag != 255, ps.AcLineStatus == 1, (ps.BatteryFlag & 8) != 0,
                    ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent : null, ps.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(ps.BatteryLifeTime) : null);
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            var drive = _drives.FirstOrDefault(d => d.Name == systemDrive) ?? _drives.FirstOrDefault();
            var gpu = _gpu.Read();
            var processesWithGpu = _processes.Select(p => p with { GpuPercent = gpu is null ? null : _gpu.Processes.GetValueOrDefault(p.Id) }).ToArray();
            return new(cpu, hasMemory ? memory.TotalPhysical - memory.AvailablePhysical : null, hasMemory ? memory.TotalPhysical : null,
                drive?.Used, drive?.Total, drive?.Name ?? systemDrive, rate.Download, rate.Upload, networks.Count, TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64()))
            {
                CpuSystemPercent = system, CpuUserPercent = userPercent, GpuPercent = gpu, GpuName = _gpuName ?? "GPU",
                CpuName = _cpuName, CommitUsed = hasMemory && memory.AvailablePageFile <= memory.TotalPageFile ? memory.TotalPageFile - memory.AvailablePageFile : null, CommitTotal = hasMemory ? memory.TotalPageFile : null,
                Processes = processesWithGpu, ProcessCount = _processCount, Drives = _drives, Adapters = adapters, Power = power,
            };
        }
    }
    private void ReadDrives(long stamp)
    {
        var drives = new List<DriveUsage>();
        foreach (var drive in DriveInfo.GetDrives())
            try { if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady) drives.Add(new(drive.Name.TrimEnd('\\'), drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _drives = drives; _driveStamp = stamp;
    }
    private void ReadProcesses(long stamp)
    {
        var current = new Dictionary<int, (double Cpu, long Started)>();
        var readings = new List<ProcessUsage>();
        double seconds = _processStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_processStamp, stamp).TotalSeconds;
        var processes = Process.GetProcesses(); _processCount = processes.Length;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0) continue;
                    double cpu = process.TotalProcessorTime.TotalSeconds; long started = process.StartTime.Ticks;
                    current[process.Id] = (cpu, started);
                    double? percent = seconds > 0 && _processTimes.TryGetValue(process.Id, out var before) && before.Started == started && cpu >= before.Cpu
                        ? Math.Clamp(100 * (cpu - before.Cpu) / (seconds * Environment.ProcessorCount), 0, 100) : null;
                    readings.Add(new(process.ProcessName, percent, process.WorkingSet64, process.Id));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        _processes = readings; _processTimes = current; _processStamp = stamp;
    }
    private static string? ReadGpuName()
    {
        try
        {
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (video is null) return null;
            foreach (string key in video.GetSubKeyNames())
            {
                using var adapter = video.OpenSubKey(key + @"\0000");
                if (adapter?.GetValue("DriverDesc") is string name && name.Length > 0 && !name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) return name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
    public void Dispose() { lock (_gate) { _disposed = true; _gpu.Dispose(); } }
}
