using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>Windows GPU Engine counters. Busiest engine, summed across its processes, never a synthetic load.</summary>
internal sealed partial class GpuUsageCounter : IDisposable
{
    private IntPtr _query, _counter;
    internal IReadOnlyDictionary<int, double> Processes { get; private set; } = new Dictionary<int, double>();
    internal void Start()
    {
        Dispose();
        if (PdhOpenQuery(null, 0, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0) { Dispose(); return; }
        PdhCollectQueryData(_query);
    }
    internal unsafe double? Read()
    {
        if (_query == IntPtr.Zero || _counter == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint bytes = 0, count = 0;
        uint result = PdhGetFormattedCounterArray(_counter, 0x200, ref bytes, ref count, IntPtr.Zero);
        if (result != 0x800007D2 || bytes == 0 || bytes > 4 * 1024 * 1024) return null;
        void* memory = NativeMemory.Alloc(bytes);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, 0x200, ref bytes, ref count, (IntPtr)memory) != 0 || count > bytes / sizeof(CounterItem)) return null;
            Dictionary<string, double> engines = new();
            Dictionary<int, double> processes = new();
            var items = (CounterItem*)memory;
            for (int i = 0; i < count; i++)
            {
                var value = items[i].Value;
                if (value.Status > 1 || !double.IsFinite(value.Number) || value.Number < 0 || items[i].Name == null) continue;
                string name = new(items[i].Name);
                int engine = name.IndexOf("_luid_", StringComparison.Ordinal);
                if (engine < 0) continue;
                if (engine > 4 && name.StartsWith("pid_", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4, engine - 4), out var pid)) processes[pid] = Math.Clamp(Math.Max(processes.GetValueOrDefault(pid), value.Number), 0, 100);
                string key = name[(engine + 1)..];
                engines[key] = engines.GetValueOrDefault(key) + value.Number;
            }
            Processes = processes;
            return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        finally { NativeMemory.Free(memory); }
    }
    public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); _query = _counter = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue { [FieldOffset(0)] internal uint Status; [FieldOffset(8)] internal double Number; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CounterItem { internal char* Name; internal CounterValue Value; }
    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? source, nuint user, out IntPtr query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(IntPtr query, string path, nuint user, out IntPtr counter);
    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr values);
    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);
}
