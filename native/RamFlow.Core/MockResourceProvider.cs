namespace RamFlow.Core;

public sealed class MockResourceProvider : IResourceProvider
{
    public Snapshot Current { get; set; } = new() { RamTotal = 8UL * 1073741824, Available = 4UL * 1073741824, Commit = 6UL * 1073741824, CommitLimit = 16UL * 1073741824, Cache = 1073741824, CpuPercent = 22, DiskPercent = 12 };
    public List<ProcessSample> Items { get; } = [new(987650, 12, "idle-demo.exe", "D:\\Apps\\idle-demo.exe", 1, 500 * 1048576UL, 400 * 1048576UL, 0, 0, true), new(987651, 13, "code.exe", "D:\\Apps\\code.exe", 1, 800 * 1048576UL, 600 * 1048576UL, 0, 0, true)];
    public (int Pid, string Name) Focus { get; set; } = (987651, "code.exe");
    public Dictionary<int, ResourceValues> Values { get; } = new();
    public int Writes { get; private set; }
    public int Trims { get; private set; }
    public bool FailWrite { get; set; }
    public bool IdentityUnknown { get; set; }
    public bool FullscreenActive { get; set; }
    public bool FailTrim { get; set; }
    public List<int> TrimmedPids { get; } = [];
    public Snapshot Sample() => Current;
    public IReadOnlyList<ProcessSample> Processes() => Items.ToArray();
    public (int Pid, string Name) Foreground() => Focus;
    public bool Fullscreen() => FullscreenActive;
    public IReadOnlyList<CpuCore> Topology() => [new(1, 0, 0, 0, false, false), new(2, 0, 1, 1, false, false)];
    public long? Identity(int pid) => IdentityUnknown ? null : Items.FirstOrDefault(x => x.Pid == pid)?.Identity;
    public bool Exited(int pid) => !Items.Any(x => x.Pid == pid);
    public bool Validate(ProcessSample p, bool restoring = false) => Identity(p.Pid) == p.Identity && (restoring || p.Pid != Focus.Pid);
    public ResourceValues? ReadValues(int pid) => Values.GetValueOrDefault(pid, new(5, 0x20, 0, 0, []));
    public bool WriteValues(int pid, ResourceValues values, long identity) { if (Identity(pid) != identity || FailWrite) return false; Writes++; Values[pid] = values; return true; }
    public bool Trim(int pid, long identity) {
        Trims++; TrimmedPids.Add(pid);
        if (FailTrim || Identity(pid) != identity) return false;
        int at = Items.FindIndex(p => p.Pid == pid);
        if (at < 0) return false;
        Items[at] = Items[at] with { WorkingSet = Items[at].WorkingSet / 2 };
        return true;
    }
    public ulong? WorkingSet(int pid, long identity) => Identity(pid) == identity ? Items.FirstOrDefault(p => p.Pid == pid)?.WorkingSet : null;
    public void Dispose() { }
}
