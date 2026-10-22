using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RamFlow.Core;

// Isolated verification harness. Never persists settings or enables intervention.
string Option(string key, string fallback) {
    int at = Array.IndexOf(args, key);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
}
string measurement = Option("--measurement", "off");
string? workload = Option("--workload", "") is { Length: > 0 } executable ? executable : null;
string[] workloadArgs = JsonSerializer.Deserialize<string[]>(Option("--workload-args", "[]")) ?? [];
if (workload is not null && measurement != "on") throw new ArgumentException("Workload smoke check requires --measurement on");
if (measurement is not ("off" or "on")) throw new ArgumentException("Use --measurement off|on");
int seconds = int.Parse(Option("--seconds", "30"));
if (seconds is < 5 or > 1800) throw new ArgumentException("Use 5..1800 seconds");
using var background = new BackgroundIo();
using var engine = new Engine(new WindowsResourceProvider(), persist: false, allowRemote: false);
engine.ApplySettings(new() { DryRun = true });
engine.Start();
await Task.Delay(4000); // Identical warmup in both builds and modes.
object? session = null;
Type? sessionType = null;
using var self = Process.GetCurrentProcess();
TimeSpan cpuStart = self.TotalProcessorTime;
long allocatedStart = GC.GetTotalAllocatedBytes();
var clock = Stopwatch.StartNew();
object? sessionResult = null;
try {
    sessionType = typeof(Engine).Assembly.GetType("RamFlow.Core.BenchmarkSession");
    if (sessionType is not null) session = Activator.CreateInstance(sessionType, engine)!;
    if (measurement == "on") {
        if (sessionType is null) throw new InvalidOperationException("Build does not contain the on-demand sampler.");
        sessionType.GetMethod("Start")!.Invoke(session, [workload, workloadArgs]);
    }
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    if (session is not null && measurement == "on") {
        var stop = (Task)sessionType!.GetMethod("StopAsync")!.Invoke(session, null)!;
        await stop;
        sessionResult = stop.GetType().GetProperty("Result")!.GetValue(stop);
    }
    self.Refresh();
    var result = new {
        Scope = "Isolated native engine, DryRun only; no WPF UI; not an intervention-effect study",
        OperatingSystem = Environment.OSVersion.VersionString,
        LogicalProcessors = Environment.ProcessorCount,
        Runtime = Environment.Version.ToString(),
        Measurement = measurement,
        ElapsedSeconds = clock.Elapsed.TotalSeconds,
        ProcessCpuMs = (self.TotalProcessorTime - cpuStart).TotalMilliseconds,
        ProcessCpuPercent = (self.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
        WorkingSetBytes = self.WorkingSet64,
        PrivateBytes = self.PrivateMemorySize64,
        AllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedStart,
        engine.Settings.DryRun,
        Session = sessionResult
    };
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    string report = Option("--report", "");
    if (report.Length > 0) await File.WriteAllTextAsync(report, json);
    Console.WriteLine(json);
} finally {
    (session as IDisposable)?.Dispose();
    engine.Stop();
}
