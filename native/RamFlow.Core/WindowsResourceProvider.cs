using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RamFlow.Core;

public sealed class WindowsResourceProvider : IResourceProvider
{
    // The UI's remote engine proxy does not sample this provider. Initialize
    // the native PDH query only in the process that actually collects telemetry.
    private Counters? counters;
    private readonly Dictionary<(int, long), (DateTimeOffset Time, double Cpu, ulong Io)> previous = new();
    private long oldIdle, oldKernel, oldUser;
    public Snapshot Sample()
    {
        var counters = this.counters ??= new Counters();
        counters.Collect();
        var memory = new WindowsNative.Memory { Length = (uint)Marshal.SizeOf<WindowsNative.Memory>() };
        var perf = new WindowsNative.Performance { Size = (uint)Marshal.SizeOf<WindowsNative.Performance>() };
        if (!WindowsNative.GlobalMemoryStatusEx(ref memory) || !WindowsNative.GetPerformanceInfo(ref perf, perf.Size)) throw new InvalidOperationException("Windows 메모리 정보를 읽을 수 없습니다.");
        WindowsNative.GetSystemTimes(out var idle, out var kernel, out var user);
        long total = kernel - oldKernel + user - oldUser;
        double cpu = oldKernel == 0 || total <= 0 ? 0 : 100 * (1 - (double)(idle - oldIdle) / total);
        (oldIdle, oldKernel, oldUser) = (idle, kernel, user);
        bool powerOk = WindowsNative.GetSystemPowerStatus(out var power);
        double? thermal = counters.ArrayValue("thermal", true);
        thermal = thermal is >= 250 and <= 450 ? thermal - 273.15 : null;
        return new Snapshot { RamTotal = memory.Total, Available = memory.Available,
            Commit = (ulong)perf.Commit * (ulong)perf.PageSize, CommitLimit = (ulong)perf.CommitLimit * (ulong)perf.PageSize,
            Cache = (ulong)perf.Cache * (ulong)perf.PageSize,
            Standby = (ulong)((counters.Value("standby") ?? 0) + (counters.Value("standby0") ?? 0) + (counters.Value("standby1") ?? 0)),
            Modified = (ulong)(counters.Value("modified") ?? 0), CompressedBytes = counters.Value("compressed"),
            CpuPercent = Math.Clamp(cpu, 0, 100), DiskPercent = counters.Value("disk") is double disk ? Math.Clamp(disk, 0, 100) : null,
            PagesInput = counters.Value("input"), PagesOutput = counters.Value("output"), PagefileUsagePercent = counters.Value("pagefile"),
            ThermalCelsius = thermal, GpuUsedBytes = counters.ArrayValue("gpu"),
            BatteryPercent = powerOk && power.Percent <= 100 ? power.Percent : null, OnBattery = powerOk && power.Ac == 0 };
    }
    public IReadOnlyList<ProcessSample> Processes()
    {
        var result = new List<ProcessSample>();
        var alive = new HashSet<(int, long)>();
        var now = DateTimeOffset.UtcNow;
        foreach (var process in Process.GetProcesses()) using (process) {
            try {
                int pid = process.Id;
                long identity = process.StartTime.ToFileTimeUtc();
                string path = ""; bool known = false; ulong io = 0;
                double totalCpu = process.TotalProcessorTime.TotalSeconds;
                try { path = process.MainModule?.FileName ?? ""; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                using var handle = WindowsNative.OpenProcess(0x1000, false, pid);
                if (!handle.IsInvalid && WindowsNative.GetProcessIoCounters(handle, out var info)) { io = info.ReadBytes + info.WriteBytes; known = true; }
                var key = (pid, identity); alive.Add(key);
                double cpu = 0, rate = 0;
                if (previous.TryGetValue(key, out var old)) {
                    double seconds = (now - old.Time).TotalSeconds;
                    if (seconds > 0) { cpu = Math.Max(0, (totalCpu - old.Cpu) / seconds / Environment.ProcessorCount * 100); rate = io >= old.Io ? (io - old.Io) / seconds : 0; }
                } else known = false; // 첫 관측만으로 비활성으로 단정하지 않는다.
                previous[key] = (now, totalCpu, io);
                result.Add(new(pid, identity, process.ProcessName + ".exe", path, process.SessionId,
                    (ulong)process.WorkingSet64, (ulong)process.PrivateMemorySize64, cpu, rate, known && path.Length > 0));
            } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        foreach (var key in previous.Keys.Except(alive).ToArray()) previous.Remove(key);
        return result;
    }
    public (int Pid, string Name) Foreground()
    {
        WindowsNative.GetWindowThreadProcessId(WindowsNative.GetForegroundWindow(), out int pid);
        try { using var p = Process.GetProcessById(pid); return (pid, p.ProcessName + ".exe"); } catch { return (0, ""); }
    }
    public bool Fullscreen() => WindowsNative.GetWindowRect(WindowsNative.GetForegroundWindow(), out var r) &&
        r.Right - r.Left >= WindowsNative.GetSystemMetrics(0) && r.Bottom - r.Top >= WindowsNative.GetSystemMetrics(1);
    public long? Identity(int pid)
    {
        using var p = WindowsNative.OpenProcess(0x1000, false, pid);
        return !p.IsInvalid && WindowsNative.GetProcessTimes(p, out var creation, out _, out _, out _) ? creation : null;
    }
    public bool Exited(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; } catch { return false; }
    }
    public bool Validate(ProcessSample target, bool restoring = false)
    {
        if (Identity(target.Pid) != target.Identity || target.Pid == Environment.ProcessId || target.Session != Process.GetCurrentProcess().SessionId) return false;
        var fg = Foreground();
        if (!restoring && (target.Pid == fg.Pid || string.Equals(target.Name, fg.Name, StringComparison.OrdinalIgnoreCase))) return false;
        try { using var p = Process.GetProcessById(target.Pid); string path = p.MainModule?.FileName ?? "";
            using var handle = WindowsNative.OpenProcess(0x1000, false, target.Pid);
            if (handle.IsInvalid || !WindowsNative.IsProcessCritical(handle, out bool critical) || critical || !WindowsNative.OpenProcessToken(handle, 8, out var token)) return false;
            using (token) using (var owner = new System.Security.Principal.WindowsIdentity(token.DangerousGetHandle())) using (var current = System.Security.Principal.WindowsIdentity.GetCurrent())
                if (owner.User != current.User) return false;
            return path.Length > 0 && !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !Policy.ProtectedNames.Contains(p.ProcessName.ToLowerInvariant()) && p.SessionId > 0 && p.SessionId == Process.GetCurrentProcess().SessionId;
        } catch { return false; }
    }
    public ResourceValues? ReadValues(int pid)
    {
        using var p = WindowsNative.OpenProcess(0x1000, false, pid);
        var eco = new WindowsNative.Throttle { Version = 1 };
        if (p.IsInvalid || !WindowsNative.GetProcessInformation(p, 0, out var memory, 4) ||
            !WindowsNative.GetThrottle(p, 4, ref eco, 12)) return null;
        var cpu = WindowsNative.GetPriorityClass(p);
        WindowsNative.GetProcessDefaultCpuSets(p, null, 0, out uint count);
        if (count > 4096 || cpu == 0) return null;
        uint[] ids = new uint[count];
        if (!WindowsNative.GetProcessDefaultCpuSets(p, ids, count, out _)) return null;
        return new(memory, cpu, eco.Control, eco.State, ids);
    }
    public bool WriteValues(int pid, ResourceValues values, long identity)
    {
        using var p = WindowsNative.OpenProcess(0x1200, false, pid);
        if (p.IsInvalid || !WindowsNative.GetProcessTimes(p, out long creation, out _, out _, out _) || creation != identity) return false;
        uint memory = values.MemoryPriority;
        var eco = new WindowsNative.Throttle { Version = 1, Control = values.PowerControl, State = values.PowerState };
        // 모두 시도해 일부 성공한 경우에도 원복할 수 있도록 호출자는 원래 값 기록을 보존한다.
        bool mem = WindowsNative.SetProcessInformation(p, 0, ref memory, 4);
        bool cpu = WindowsNative.SetPriorityClass(p, values.CpuPriority);
        bool power = WindowsNative.SetThrottle(p, 4, ref eco, 12);
        bool sets = WindowsNative.SetProcessDefaultCpuSets(p, values.CpuSets, (uint)values.CpuSets.Length);
        return mem && cpu && power && sets;
    }
    public bool Trim(int pid, long identity)
    {
        using var p = WindowsNative.OpenProcess(0x1100, false, pid);
        return !p.IsInvalid && WindowsNative.GetProcessTimes(p, out long creation, out _, out _, out _) && creation == identity && WindowsNative.EmptyWorkingSet(p);
    }
    public ulong? WorkingSet(int pid, long identity)
    {
        try {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToFileTimeUtc() != identity) return null;
            ulong bytes = (ulong)process.WorkingSet64;
            return Identity(pid) == identity ? bytes : null;
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }
    public IReadOnlyList<CpuCore> Topology()
    {
        WindowsNative.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint size, IntPtr.Zero, 0);
        if (size == 0 || size > 1048576) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try {
            if (!WindowsNative.GetSystemCpuSetInformation(buffer, size, out _, IntPtr.Zero, 0)) return [];
            var result = new List<CpuCore>();
            for (int at = 0; at + 20 <= size;) {
                var p = buffer + at; int length = Marshal.ReadInt32(p);
                if (length < 20 || at + length > size) break;
                if (Marshal.ReadInt32(p, 4) == 0) {
                    byte flags = Marshal.ReadByte(p, 19);
                    result.Add(new((uint)Marshal.ReadInt32(p, 8), (ushort)Marshal.ReadInt16(p, 12), Marshal.ReadByte(p, 14), Marshal.ReadByte(p, 18), (flags & 1) != 0, (flags & 2) != 0));
                } at += length;
            } return result;
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() => counters?.Dispose();
}
