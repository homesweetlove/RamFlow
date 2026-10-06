using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RamFlow.Core;

internal static class WindowsNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Memory { public uint Length, Load; public ulong Total, Available, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, Extended; }
    [StructLayout(LayoutKind.Sequential)] internal struct Performance { public uint Size; public nuint Commit, CommitLimit, CommitPeak, Physical, Available, Cache, Kernel, Paged, NonPaged, PageSize; public uint Handles, Processes, Threads; }
    [StructLayout(LayoutKind.Sequential)] internal struct Power { public byte Ac, Flags, Percent, Saver; public uint Life, FullLife; }
    [StructLayout(LayoutKind.Sequential)] internal struct Io { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct Throttle { public uint Version, Control, State; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] internal struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] internal static extern bool GlobalMemoryStatusEx(ref Memory info);
    [DllImport("psapi.dll")] internal static extern bool GetPerformanceInfo(ref Performance info, uint size);
    [DllImport("kernel32.dll")] internal static extern bool GetSystemPowerStatus(out Power info);
    [DllImport("kernel32.dll")] internal static extern bool GetProcessIoCounters(SafeProcessHandle process, out Io info);
    [DllImport("kernel32.dll")] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] internal static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("advapi32.dll")] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] internal static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] internal static extern bool GetProcessInformation(SafeProcessHandle process, int kind, out uint data, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation")] internal static extern bool GetThrottle(SafeProcessHandle process, int kind, ref Throttle data, uint size);
    [DllImport("kernel32.dll")] internal static extern bool SetProcessInformation(SafeProcessHandle process, int kind, ref uint data, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation")] internal static extern bool SetThrottle(SafeProcessHandle process, int kind, ref Throttle data, uint size);
    [DllImport("kernel32.dll")] internal static extern uint GetPriorityClass(SafeProcessHandle process);
    [DllImport("kernel32.dll")] internal static extern bool SetPriorityClass(SafeProcessHandle process, uint priority);
    [DllImport("psapi.dll")] internal static extern bool EmptyWorkingSet(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessDefaultCpuSets(SafeProcessHandle process, [Out] uint[]? ids, uint count, out uint required);
    [DllImport("kernel32.dll")] internal static extern bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint[]? ids, uint count);
    [DllImport("kernel32.dll")] internal static extern bool GetSystemCpuSetInformation(IntPtr info, uint size, out uint required, IntPtr process, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQuery(string? source, nuint context, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhAddEnglishCounter(IntPtr query, string path, nuint context, out IntPtr counter);
    [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class Counters : IDisposable
{
    private IntPtr query;
    private readonly Dictionary<string, IntPtr> counters = new();
    public Counters()
    {
        if (WindowsNative.PdhOpenQuery(null, 0, out query) != 0) return;
        var paths = new Dictionary<string, string> {
            ["disk"] = @"\PhysicalDisk(_Total)\% Disk Time", ["input"] = @"\Memory\Pages Input/sec",
            ["output"] = @"\Memory\Pages Output/sec", ["standby"] = @"\Memory\Standby Cache Normal Priority Bytes",
            ["standby0"] = @"\Memory\Standby Cache Reserve Bytes", ["standby1"] = @"\Memory\Standby Cache Core Bytes",
            ["modified"] = @"\Memory\Modified Page List Bytes", ["pagefile"] = @"\Paging File(_Total)\% Usage",
            ["compressed"] = @"\Memory\Compressed Page Size", ["gpu"] = @"\GPU Adapter Memory(*)\Dedicated Usage",
            ["thermal"] = @"\Thermal Zone Information(*)\Temperature" };
        foreach (var (key, path) in paths)
            if (WindowsNative.PdhAddEnglishCounter(query, path, 0, out var counter) == 0) counters[key] = counter;
        WindowsNative.PdhCollectQueryData(query);
    }
    public void Collect() { if (query != IntPtr.Zero) WindowsNative.PdhCollectQueryData(query); }
    public double? Value(string key)
    {
        if (!counters.TryGetValue(key, out var handle)) return null;
        if (WindowsNative.PdhGetFormattedCounterValue(handle, 0x200, out _, out var value) != 0 || value.Status > 1 || !double.IsFinite(value.Value)) return null;
        return Math.Max(0, value.Value);
    }
    public double? ArrayValue(string key, bool maximum = false)
    {
        if (!counters.TryGetValue(key, out var handle)) return null;
        uint size = 0;
        WindowsNative.PdhGetFormattedCounterArrayW(handle, 0x200, ref size, out _, IntPtr.Zero);
        if (size == 0 || size > 1048576) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try {
            if (WindowsNative.PdhGetFormattedCounterArrayW(handle, 0x200, ref size, out var count, buffer) != 0) return null;
            var values = new List<double>();
            for (int i = 0; i < count && (i + 1) * 24 <= size; i++) {
                var value = Marshal.PtrToStructure<WindowsNative.CounterValue>(buffer + i * 24 + 8);
                if (value.Status <= 1 && double.IsFinite(value.Value)) values.Add(value.Value);
            }
            return values.Count == 0 ? null : maximum ? values.Max() : values.Sum();
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() { if (query != IntPtr.Zero) { WindowsNative.PdhCloseQuery(query); query = IntPtr.Zero; } }
}
