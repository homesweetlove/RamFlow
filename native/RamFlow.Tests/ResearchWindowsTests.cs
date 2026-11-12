using System.Diagnostics;
using RamFlow.Core;

static class ResearchWindowsTests
{
    public static void Run(Action<bool, string> check) {
        if (Process.GetCurrentProcess().SessionId <= 0) {
            Console.WriteLine("SKIP CARP owned-child engine test: no interactive user session; session-zero protection remains enabled."); return;
        }
        string executable = Path.Combine(AppContext.BaseDirectory, "RamFlow.Tests.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--research-disposable-child");
        using var child = Process.Start(start) ?? throw new IOException("Disposable child did not start.");
        using var real = new WindowsResourceProvider();
        try {
            long identity = real.Identity(child.Id) ?? throw new IOException("Disposable identity unavailable.");
            using var controlled = new OwnedOnly(real, child.Id, identity, executable);
            var original = real.ReadValues(child.Id) ?? throw new IOException("Disposable resource lookup failed.");
            using (var engine = new Engine(controlled, persist: false, allowRemote: false, observations: new CpuFixture())) {
                engine.ApplySettings(new() { PolicyName = "carp", DryRun = false, BackgroundApps = [executable], Carp = new() { StableSamples = 1 } });
                engine.Tick();
                check(real.ReadValues(child.Id)?.CpuPriority == 0x4000, "CARP Windows owned-child CPU application failed");
                check(controlled.TrimRequests == 0 && controlled.OutOfScopeRequests == 0, "CARP Windows attempted trim or nonowned PID");
                engine.Stop();
                var restored = real.ReadValues(child.Id)!;
                check(restored.CpuPriority == original.CpuPriority && restored.MemoryPriority == original.MemoryPriority &&
                    restored.PowerControl == original.PowerControl && restored.PowerState == original.PowerState && restored.CpuSets.SequenceEqual(original.CpuSets),
                    "CARP Windows owned-child original settings were not restored");
            }
            Console.WriteLine("CARP Windows owned-child application/restoration passed (synthetic policy input; no performance result).");
        } finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
    }
    sealed class CpuFixture : IPolicyObservationSource {
        public CarpEvidence? Read(ProcessSample p, Snapshot s) => new(p.Pid, p.Identity, s.Time,
            "synthetic-safety-fixture-not-calibrated", ContentionKind.Cpu, 3, [new(ResourceAction.LowerCpu, .4, .1, 0, .01, 1)]);
    }
    sealed class OwnedOnly(WindowsResourceProvider real, int pid, long identity, string path) : IResourceProvider {
        public int OutOfScopeRequests, TrimRequests;
        // Only contention and app activity are synthetic. Actual setters, PID
        // identity, owner/session/path validation and resource readback are real.
        public Snapshot Sample() => real.Sample() with { CpuPercent = 95, Available = 4UL * 1073741824, RamTotal = 16UL * 1073741824, Commit = 4UL * 1073741824, CommitLimit = 32UL * 1073741824, PagesInput = 0, Standby = 0 };
        public IReadOnlyList<ProcessSample> Processes() => [new(pid, identity, Path.GetFileName(path), path, Process.GetCurrentProcess().SessionId, 32 * 1048576, 16 * 1048576, 30, 0, true)];
        public (int Pid, string Name) Foreground() => (Environment.ProcessId, "synthetic-foreground-for-safety-test.exe");
        public bool Fullscreen() => false;
        public IReadOnlyList<CpuCore> Topology() => real.Topology();
        public long? Identity(int id) => real.Identity(id);
        public bool Exited(int id) => real.Exited(id);
        public bool Validate(ProcessSample p, bool restoring = false) => p.Pid == pid && p.Identity == identity && real.Validate(p, restoring);
        public ResourceValues? ReadValues(int id) => id == pid ? real.ReadValues(id) : null;
        public bool WriteValues(int id, ResourceValues values, long creation) {
            if (id != pid || creation != identity) { OutOfScopeRequests++; return false; }
            return real.WriteValues(id, values, creation);
        }
        public bool Trim(int id, long creation) { TrimRequests++; return false; }
        public ulong? WorkingSet(int id, long creation) => real.WorkingSet(id, creation);
        public void Dispose() { }
    }
}
