namespace RamFlow.Core;

public sealed record Settings
{
    public bool DryRun { get; init; } = true;
    public bool Paused { get; init; }
    public string Mode { get; init; } = "auto";
    public bool CpuManagement { get; init; } = true;
    public bool EcoQos { get; init; } = true;
    public bool CpuSets { get; init; }
    public bool WorkingSetTrim { get; init; } = true;
    public bool Learning { get; init; }
    public string[] Whitelist { get; init; } = [];
}
public enum PressureLevel { Normal, Moderate, High, Severe, Critical }
public sealed record CpuCore(uint Id, ushort Group, byte LogicalIndex, byte Efficiency, bool Parked, bool Allocated);
public sealed record Snapshot
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
    public ulong RamTotal { get; init; }
    public ulong Available { get; init; }
    public ulong Commit { get; init; }
    public ulong CommitLimit { get; init; }
    public ulong Cache { get; init; }
    public ulong Standby { get; init; }
    public ulong Modified { get; init; }
    public double? CompressedBytes { get; init; }
    public double CpuPercent { get; init; }
    public double? DiskPercent { get; init; }
    public double? PagesInput { get; init; }
    public double? PagesOutput { get; init; }
    public double? PagefileUsagePercent { get; init; }
    public double? ThermalCelsius { get; init; }
    public double? GpuUsedBytes { get; init; }
    public int? BatteryPercent { get; init; }
    public bool OnBattery { get; init; }
}
public sealed record ProcessSample(int Pid, long Identity, string Name, string Path, int Session,
    ulong WorkingSet, ulong PrivateBytes, double CpuPercent, double IoBytesPerSecond, bool ActivityKnown);
public sealed record ProcessRow(int Pid, long Identity, string Name, ulong WorkingSet, ulong PrivateBytes,
    string Activity, string? ProtectedReason, double CpuPercent, double IoBytesPerSecond);
public sealed record LogEntry(DateTimeOffset Time, string Message, bool DryRun = false);
public sealed record State(Snapshot Snapshot, double PressureScore, PressureLevel Level, string Mode,
    string Foreground, IReadOnlyList<ProcessRow> Processes, IReadOnlyList<LogEntry> Events,
    int PendingRestores, IReadOnlyList<CpuCore> CpuTopology, IReadOnlyList<string> Predictions)
{
    public int EnginePid { get; init; } = Environment.ProcessId;
    public double SystemPressure
    {
        get {
            double score = PressureScore * .45 + Snapshot.CpuPercent * .25, weight = .7;
            if (Snapshot.DiskPercent is double disk) { score += disk * .2; weight += .2; }
            if (Snapshot.ThermalCelsius is double thermal) { score += Math.Clamp((thermal - 60) * 2.5, 0, 100) * .05; weight += .05; }
            if (Snapshot.OnBattery && Snapshot.BatteryPercent is int battery) { score += Math.Clamp((30 - battery) * 4, 0, 100) * .05; weight += .05; }
            return Math.Clamp(score / weight, 0, 100);
        }
    }
}
public sealed record ResourceValues(uint MemoryPriority, uint CpuPriority, uint PowerControl, uint PowerState, uint[] CpuSets);
public interface IResourceProvider : IDisposable
{
    Snapshot Sample();
    IReadOnlyList<ProcessSample> Processes();
    (int Pid, string Name) Foreground();
    bool Fullscreen();
    IReadOnlyList<CpuCore> Topology();
    long? Identity(int pid);
    bool Exited(int pid);
    bool Validate(ProcessSample target, bool restoring = false);
    ResourceValues? ReadValues(int pid);
    bool WriteValues(int pid, ResourceValues values, long identity);
    bool Trim(int pid, long identity);
}
public static class Display
{
    public static string Bytes(double value) => value >= 1073741824 ? $"{value / 1073741824:F1} GB" : $"{value / 1048576:F0} MB";
    public static readonly string[] Modes = ["auto", "developer", "gaming", "ai", "browser", "office", "media", "battery", "background"];
}
