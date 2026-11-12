using System.Diagnostics;
using System.Text.Json;
using RamFlow.Core;

namespace ResearchLab;

static class Options
{
    public static string Get(string[] args, string key, string fallback) { int at = Array.LastIndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
    public static string[] Replace(string[] args, string key, string value) { var list = new List<string>(); for (int i = 0; i < args.Length; i++) {
        if (args[i] == key) { i++; continue; } list.Add(args[i]); } list.Add(key); list.Add(value); return list.ToArray(); }
    public static int Seed(string[] args) => (int)(ulong.Parse(Get(args, "--seed", "1"), System.Globalization.CultureInfo.InvariantCulture) % int.MaxValue);
    public static int Int(string[] args, string key, int fallback, int min, int max) { int value = int.Parse(Get(args, key, fallback.ToString())); if (value < min || value > max) throw new ArgumentException($"{key} must be {min}..{max}."); return value; }
    public static void NewDirectory(string path) { if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Preserve the existing run; choose a new output directory."); Directory.CreateDirectory(path); }
    public static void WriteNew<T>(string path, T value) { using var file = new FileStream(path, FileMode.CreateNew); JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
    public static string Hardware(Snapshot s) => FileResearchAuditSink.Hash(new { s.RamTotal, Environment.ProcessorCount,
        Cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "unknown"),
        Os = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() });
    public static object CaptureSources(string directory) {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !System.IO.Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName,".git"))) root = root.Parent;
        if (root is null) return new { Status = "unavailable", Note = "No Git checkout found; binary hashes remain recorded." };
        string Query(params string[] args) { var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(root.FullName); foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; string text = process.StandardOutput.ReadToEnd(); process.WaitForExit(); return process.ExitCode == 0 ? text.Trim() : "unavailable"; }
        var files = new Dictionary<string, string?>();
        foreach (string relative in new[] { "native/RamFlow.Core", "scripts/ResearchLab" }) foreach (string path in System.IO.Directory.GetFiles(Path.Combine(root.FullName, relative), "*", SearchOption.TopDirectoryOnly)
            .Where(x => x.EndsWith(".cs") || x.EndsWith(".csproj"))) {
            string name = Path.GetRelativePath(root.FullName, path), target = Path.Combine(directory, "source", name); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target); files[name] = FileResearchAuditSink.FileHash(target);
        }
        foreach(string relative in new[] { "native/Directory.Build.props","scripts/research_lab_adapter.py","scripts/research_matrix.py","scripts/research_analyze.py","scripts/research_logcheck.py","scripts/research_app_pilot.py","scripts/research_app_analyze.py","scripts/test_research_apps.py","scripts/research_cpu_preflight.py","scripts/test_research_cpu_worker_windows.py" }) {
            string path=Path.Combine(root.FullName,relative);if(!File.Exists(path))continue;
            string target=Path.Combine(directory,"source",relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(path,target);files[relative]=FileResearchAuditSink.FileHash(target);
        }
        string appAssets=Path.Combine(root.FullName,"scripts","real_apps");
        foreach(string relative in new[]{"scripts/owned_process_qos.py","scripts/research_cpu_diagnose.py","scripts/cpu_native_counters.py","scripts/research_app_batch.py","scripts/research_budget_batch.py","scripts/research_budget_check.py"}) {
            string path=Path.Combine(root.FullName,relative),target=Path.Combine(directory,"source",relative);
            if(File.Exists(path)){Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(path,target);files[relative]=FileResearchAuditSink.FileHash(target);}
        }
        if(Directory.Exists(appAssets))foreach(string path in Directory.GetFiles(appAssets,"*",SearchOption.AllDirectories).Where(p=>!p.Contains("__pycache__") && !p.EndsWith(".pyc"))) {
            string relative=Path.GetRelativePath(root.FullName,path),target=Path.Combine(directory,"source",relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(path,target);files[relative]=FileResearchAuditSink.FileHash(target);
        }
        return new { Status = "recorded", Commit = Query("rev-parse", "HEAD"), Dirty = Query("status", "--porcelain") != "", SourceFilesSha256 = FileResearchAuditSink.Hash(files), Files = files };
    }
}

static class LabProfiles
{
    public static CarpOptions Options(string variant) {
        if (variant == "None") return new();
        // The policy owns the approved profiles. Never manufacture safety-off
        // options from CLI names or silently fall back to an unknown profile.
        if (!Enum.TryParse<CarpAblation>(variant, false, out var selected) || !Enum.IsDefined(selected))
            throw new ArgumentException("Unknown CARP ablation profile.");
        var options = new CarpOptions { Ablation = selected }; CarpOptions.Validate(options); return options;
    }
}

sealed class OwnedProvider : IResourceProvider
{
    readonly WindowsResourceProvider real = new(); readonly FixtureChild foreground, background;
    public int OutOfScopeRequests { get; private set; }
    public bool ProbeForeground { get; set; }
    (double Cpu, ulong Io, long Stamp)? previous;
    public OwnedProvider(FixtureChild foreground, FixtureChild background) { this.foreground = foreground; this.background = background; }
    public Snapshot Sample() => real.Sample();
    public IReadOnlyList<ProcessSample> Processes() {
        var p = background.Process; p.Refresh(); long stamp = Stopwatch.GetTimestamp(); double cpu = p.TotalProcessorTime.TotalSeconds;
        bool known = OwnedIo.GetProcessIoCounters(p.SafeHandle, out var io); ulong bytes = io.ReadBytes + io.WriteBytes;
        double seconds = previous is {} old ? (stamp - old.Stamp) / (double)Stopwatch.Frequency : 0;
        double rate = previous is {} prior && seconds > 0 ? Math.Max(0, (cpu - prior.Cpu) / seconds / Environment.ProcessorCount * 100) : 0;
        double ioRate = previous is {} before && seconds > 0 && bytes >= before.Io ? (bytes - before.Io) / seconds : 0;
        previous = (cpu, bytes, stamp);
        return [new(p.Id, background.Identity, p.ProcessName + ".exe", p.MainModule?.FileName ?? "", p.SessionId,
            (ulong)p.WorkingSet64, (ulong)p.PrivateMemorySize64, rate, ioRate, known && seconds > 0)];
    }
    public (int Pid, string Name) Foreground() { var p = ProbeForeground ? background.Process : foreground.Process; return (p.Id, p.ProcessName + ".exe"); }
    public bool Fullscreen() => false;
    public IReadOnlyList<CpuCore> Topology() => real.Topology();
    public long? Identity(int pid) => real.Identity(pid);
    public bool Exited(int pid) => real.Exited(pid);
    bool Owns(int pid, long identity) => pid == background.Process.Id && identity == background.Identity;
    public bool Validate(ProcessSample target, bool restoring = false) => Owns(target.Pid, target.Identity) && real.Validate(target, restoring);
    public ResourceValues? ReadValues(int pid) => pid == background.Process.Id ? real.ReadValues(pid) : null;
    public bool WriteValues(int pid, ResourceValues values, long identity) { if (!Owns(pid, identity)) { OutOfScopeRequests++; return false; } return real.WriteValues(pid, values, identity); }
    public bool Trim(int pid, long identity) { OutOfScopeRequests++; return false; }
    public ulong? WorkingSet(int pid, long identity) => Owns(pid, identity) ? real.WorkingSet(pid, identity) : null;
    public void Dispose() => real.Dispose();
}

static class OwnedIo
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct Counters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out Counters counters);
}

sealed class CandidatePolicy(ResourceAction action) : IResourcePolicy
{
    public string Name => "measured-candidate";
    public string Version => "1.0";
    public PolicyDecision Decide(PolicyInput input) {
        string? protection = PolicySafety.Protection(input, explicitOnly: true);
        return new(protection is null ? action : ResourceAction.None, protection ?? "calibration-candidate-only", PolicySafety.Classify(input), []);
    }
}

sealed class TimedAudit : IResearchAuditSink, IDisposable
{
    readonly FileResearchAuditSink sink;
    public List<double> DecisionMs { get; } = [];
    public List<double> WriteMs { get; } = [];
    public List<string> RestoreResults { get; } = [];
    public List<object> TimingSamples { get; } = [];
    public string Phase { get; set; } = "evaluate";
    public int Applies { get; private set; }
    public TimedAudit(string path, string commit) { sink = new(path, commit); }
    public void Write(ResearchAuditEvent entry) {
        var clock = Stopwatch.StartNew(); sink.Write(entry); double writeMs = clock.Elapsed.TotalMilliseconds; WriteMs.Add(writeMs);
        double? measuredDecision = null;
        if (entry.Kind == "decision" && entry.Detail is not null) {
            var detail = JsonSerializer.SerializeToElement(entry.Detail);
            if (detail.TryGetProperty("DecisionElapsedMs", out var time)) { measuredDecision = time.GetDouble(); DecisionMs.Add(measuredDecision.Value); }
        }
        string phase = entry.Kind.StartsWith("restore") ? "restore" : entry.Kind.StartsWith("apply") ? "intervene" : Phase;
        TimingSamples.Add(new { phase, role = "engine-host", cpu_percent = (double?)null, private_bytes = (long?)null, working_set_bytes = (long?)null,
            decision_ms = measuredDecision, log_write_ms = writeMs, time_utc = DateTimeOffset.UtcNow, status = "timing-only", measurement_kind = "instrumented-host-method" });
        if (entry.Kind == "apply" && entry.Result == "applied") Applies++;
        if (entry.Kind == "restore") RestoreResults.Add(entry.Result);
    }
    public void Dispose() => sink.Dispose();
}

sealed class ResourceObserver : IDisposable
{
    readonly Dictionary<int, (double Cpu, long Stamp)> previous = new();
    readonly StreamWriter raw;
    public List<object> Samples { get; } = [];
    public ResourceObserver(string path) { raw = new(new FileStream(path, FileMode.CreateNew)) { AutoFlush = true }; }
    public void Sample(Process process, string phase, string role, double? decision = null, double? log = null) {
        try {
            process.Refresh(); long stamp = Stopwatch.GetTimestamp(); double cpu = process.TotalProcessorTime.TotalSeconds;
            double? percent = previous.TryGetValue(process.Id, out var before) ?
                Math.Max(0, (cpu - before.Cpu) / ((stamp - before.Stamp) / (double)Stopwatch.Frequency) / Environment.ProcessorCount * 100) : null;
            previous[process.Id] = (cpu, stamp);
            var sample = new { time_utc = DateTimeOffset.UtcNow, pid = process.Id, creation_filetime = process.StartTime.ToFileTimeUtc(), phase, role,
                cpu_percent = percent, private_bytes = process.PrivateMemorySize64, working_set_bytes = process.WorkingSet64,
                decision_ms = decision, log_write_ms = log };
            Samples.Add(sample); raw.WriteLine(JsonSerializer.Serialize(sample));
        } catch (Exception e) { raw.WriteLine(JsonSerializer.Serialize(new { phase, role, error = e.Message, status = "unavailable" })); }
    }
    public void Dispose() => raw.Dispose();
}
