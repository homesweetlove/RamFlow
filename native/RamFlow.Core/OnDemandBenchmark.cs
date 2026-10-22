using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("RamFlow.Tests")]

namespace RamFlow.Core;

public enum SessionBenchmarkStopReason { ManualStop, WorkloadExited, MaximumDuration, Disposed, Error }

public sealed record SessionProcessMetrics(int Pid, double? CpuPercent, long WorkingSetBytes, long PrivateBytes);
public sealed record SessionBenchmarkSample(DateTimeOffset Time, double ElapsedMs, Snapshot System,
    SessionProcessMetrics? UiProcess, SessionProcessMetrics? EngineProcess);

public sealed record SessionBenchmarkResult
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public double DurationMs { get; init; }
    public SessionBenchmarkStopReason StopReason { get; init; }
    public bool WorkloadCompleted { get; init; }
    public double? WorkloadDurationMs { get; init; }
    public int? WorkloadExitCode { get; init; }
    public Snapshot? Before { get; init; }
    public Snapshot? After { get; init; }
    public Settings? SettingsBefore { get; init; }
    public Settings? SettingsAfter { get; init; }
    public bool SettingsChanged { get; init; }
    public bool IsValid { get; init; }
    public int SampleCount { get; init; }
    public int SkippedSamples { get; init; }
    public IReadOnlyList<SessionBenchmarkSample> Samples { get; init; } = [];
    public double SamplingCpuMs { get; init; }
    public double SamplingWallMs { get; init; }
    public long SamplingAllocatedBytes { get; init; }
    public string[] Notes { get; init; } = [];
}

/// <summary>Opt-in observation only. Construction retains the engine and starts no work.</summary>
public sealed class BenchmarkSession : IDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PressureBackoff = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(30);
    public const int MaximumRetainedSamples = 256;
    private readonly Engine engine;
    private readonly Func<BenchmarkSource>? sourceFactory;
    private Run? active;
    private bool disposed;
    private int running;

    public BenchmarkSession(Engine engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    internal BenchmarkSession(Engine engine, Func<BenchmarkSource> sourceFactory) : this(engine) => this.sourceFactory = sourceFactory;
    public bool IsRunning => Volatile.Read(ref running) != 0;

    private sealed class Run
    {
        internal readonly ManualResetEvent Stop = new(false);
        internal readonly TaskCompletionSource<SessionBenchmarkResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SessionBenchmarkStopReason? Requested;
        internal DateTimeOffset? StopAt;
        internal BenchmarkSource? Source;
    }

    public void Start(string? executable = null, string[]? arguments = null)
    {
        // This monitor is used only for lifecycle transitions, never for sampling or IPC.
        lock (this) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsRunning) throw new InvalidOperationException("A benchmark session is already running.");
            ProcessStartInfo? workload = ValidateWorkload(executable, arguments);
            var run = new Run();
            var thread = new Thread(() => Measure(run, workload)) { IsBackground = true, Name = "RamFlow benchmark", Priority = ThreadPriority.Lowest };
            active = run; Volatile.Write(ref running, 1);
            try { thread.Start(); }
            catch { active = null; Volatile.Write(ref running, 0); run.Stop.Dispose(); throw; }
        }
    }

    public Task<SessionBenchmarkResult> StopAsync()
    {
        lock (this) {
            if (active is null) throw new InvalidOperationException("Start the benchmark first.");
            RequestStop(active, SessionBenchmarkStopReason.ManualStop);
            return active.Completion.Task;
        }
    }

    private void RequestStop(Run run, SessionBenchmarkStopReason reason)
    {
        if (!IsRunning || run.Requested.HasValue) return;
        run.StopAt = run.Source?.UtcNow ?? DateTimeOffset.UtcNow; run.Requested = reason; run.Stop.Set();
    }

    public void Dispose()
    {
        Task<SessionBenchmarkResult>? completion;
        lock (this) {
            if (disposed) return;
            disposed = true;
            if (active is not null) RequestStop(active, SessionBenchmarkStopReason.Disposed);
            completion = active?.Completion.Task;
        }
        // IPC has a bounded timeout; allow in-flight observer reads to finish before
        // the owner disposes the engine. Never block while holding the lifecycle monitor.
        if (completion is not null && Thread.CurrentThread.Name != "RamFlow benchmark") completion.Wait(TimeSpan.FromSeconds(4));
    }

    private static ProcessStartInfo? ValidateWorkload(string? executable, string[]? arguments)
    {
        if (executable is null) {
            if (arguments is { Length: > 0 }) throw new ArgumentException("Arguments require an executable.", nameof(arguments));
            return null;
        }
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an existing executable using its full .exe path.", nameof(executable));
        if (arguments is { Length: > 256 } || (arguments?.Any(x => x is null || x.Contains('\0')) ?? false) ||
            (arguments?.Sum(x => (long)x.Length + 3) ?? 0) + executable.Length > 30000)
            throw new ArgumentException("Invalid or excessive workload arguments.", nameof(arguments));
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments ?? []) info.ArgumentList.Add(argument);
        return info;
    }

    private void Measure(Run run, ProcessStartInfo? launch)
    {
        var samples = new Queue<SessionBenchmarkSample>(MaximumRetainedSamples);
        var notes = new List<string>();
        Snapshot? before = null, after = null;
        Settings? initial = null, final = null;
        bool changed = false, completed = false;
        int accepted = 0, skipped = 0;
        double cpuMs = 0, wallMs = 0;
        long allocated = 0;
        double? workloadMs = null;
        int? exitCode = null;
        var started = DateTimeOffset.UtcNow;
        var ended = started;
        double duration = 0;
        var reason = SessionBenchmarkStopReason.Error;
        BenchmarkSource? source = null;
        BenchmarkWorkload? workload = null;
        long workStart = Stopwatch.GetTimestamp(), allocationStart = GC.GetAllocatedBytesForCurrentThread();
        double cpuStart = ThreadCpuMs();
        bool measuring = true;
        try {
            source = sourceFactory?.Invoke() ?? new NativeBenchmarkSource(engine);
            lock (this) run.Source = source;
            started = source.UtcNow - source.Elapsed;
            initial = source.ReadSettings();
            // A stop immediately after Start must not launch an unwanted workload.
            lock (this) {
                if (launch is not null && !run.Requested.HasValue) workload = source.Launch(launch);
            }
            while (true) {
                DateTimeOffset? exit = workload?.ExitTime;
                SessionBenchmarkStopReason? requested;
                DateTimeOffset? stopAt;
                lock (this) { requested = run.Requested; stopAt = run.StopAt; }
                // Disposal must not issue fresh IPC against an engine the owner is now disposing.
                if (requested != SessionBenchmarkStopReason.Disposed) {
                    final = source.ReadSettings();
                    changed |= !SameSettings(initial, final);
                }
                var cutoff = started + MaximumDuration;
                if (stopAt.HasValue && stopAt.Value < cutoff) cutoff = stopAt.Value;
                if (exit.HasValue && exit <= cutoff) {
                    completed = true; workloadMs = workload!.DurationMs; exitCode = workload.ExitCode;
                    reason = SessionBenchmarkStopReason.WorkloadExited;
                } else if (requested.HasValue) reason = requested.Value;
                else if (source.Elapsed >= MaximumDuration) reason = SessionBenchmarkStopReason.MaximumDuration;
                else {
                    var sample = source.Read();
                    before ??= sample.System; after = sample.System;
                    bool pressure = UnderPressure(sample.System);
                    if (pressure) skipped++;
                    else {
                        accepted++;
                        if (samples.Count == MaximumRetainedSamples) samples.Dequeue();
                        samples.Enqueue(sample);
                    }
                    EndWork();
                    TimeSpan delay = pressure ? PressureBackoff : SampleInterval;
                    TimeSpan remaining = MaximumDuration - source.Elapsed;
                    if (remaining > TimeSpan.Zero) source.Wait(run.Stop, delay < remaining ? delay : remaining);
                    BeginWork();
                    continue;
                }
                break;
            }
        } catch (Exception e) {
            notes.Add("Measurement failed: " + e.Message); reason = SessionBenchmarkStopReason.Error;
        } finally {
            if (!measuring) BeginWork();
            if (source is not null) { ended = source.UtcNow; duration = source.Elapsed.TotalMilliseconds; }
            try { workload?.Dispose(); source?.Dispose(); }
            catch (Exception e) { notes.Add("Observer cleanup failed: " + e.Message); reason = SessionBenchmarkStopReason.Error; }
            EndWork();
        }
        if (changed) notes.Add("Settings changed during observation; this session is not comparable.");
        if (initial?.DryRun == true) notes.Add("DryRun was enabled; this observation does not measure applied optimization.");
        if (skipped > 0) notes.Add("Detailed observations were skipped under pressure; polling backed off to ten seconds.");
        if (launch is not null && !completed) notes.Add("Workload incomplete at session end; it was not terminated.");
        notes.Add("Observational data includes natural variation; it does not establish causation or application input/frame latency.");
        notes.Add("Settings validity is checked at observation boundaries; changes reverted between checks cannot be detected.");
        notes.Add("Sampler CPU/allocation counters cover this observer thread; wall time includes IPC waits. IPC server work is included in engine process metrics.");
        var result = new SessionBenchmarkResult {
            StartedAt = started, EndedAt = ended, DurationMs = duration, StopReason = reason,
            WorkloadCompleted = completed, WorkloadDurationMs = workloadMs, WorkloadExitCode = exitCode,
            Before = before, After = after, SettingsBefore = initial, SettingsAfter = final,
            SettingsChanged = changed, IsValid = !changed && accepted >= 2 && reason != SessionBenchmarkStopReason.Error && (launch is null || completed),
            SampleCount = accepted, SkippedSamples = skipped, Samples = Array.AsReadOnly(samples.ToArray()),
            SamplingCpuMs = cpuMs, SamplingWallMs = wallMs, SamplingAllocatedBytes = allocated, Notes = notes.ToArray()
        };
        lock (this) { Volatile.Write(ref running, 0); run.Stop.Dispose(); run.Completion.TrySetResult(result); }

        void BeginWork() { workStart = Stopwatch.GetTimestamp(); allocationStart = GC.GetAllocatedBytesForCurrentThread(); cpuStart = ThreadCpuMs(); measuring = true; }
        void EndWork() {
            wallMs += Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
            allocated += Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationStart);
            cpuMs += Math.Max(0, ThreadCpuMs() - cpuStart); measuring = false;
        }
    }

    internal static bool SameSettings(Settings a, Settings b) =>
        a.DryRun == b.DryRun && a.Paused == b.Paused && a.Mode == b.Mode && a.CpuManagement == b.CpuManagement &&
        a.EcoQos == b.EcoQos && a.CpuSets == b.CpuSets && a.WorkingSetTrim == b.WorkingSetTrim &&
        a.Learning == b.Learning && a.Whitelist.SequenceEqual(b.Whitelist, StringComparer.OrdinalIgnoreCase);

    internal static bool UnderPressure(Snapshot snapshot) => snapshot.CpuPercent >= 85 ||
        snapshot.RamTotal > 0 && (double)snapshot.Available / snapshot.RamTotal < .10;

    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    private static double ThreadCpuMs() => OperatingSystem.IsWindows() && GetThreadTimes(new IntPtr(-2), out _, out _, out long kernel, out long user) ? (kernel + user) / 10000d : 0;

    /// <summary>Explicit export after observation. The path is a base name; writes .json and .csv on a worker.</summary>
    public static Task Export(SessionBenchmarkResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        return Task.Run(() => {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full + ".json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            using var csv = new StreamWriter(full + ".csv", false, Encoding.UTF8);
            csv.WriteLine("time,elapsed_ms,available_bytes,commit_bytes,cpu_percent,pages_input_per_second,ui_pid,ui_cpu_percent,ui_working_set_bytes,ui_private_bytes,engine_pid,engine_cpu_percent,engine_working_set_bytes,engine_private_bytes,stop_reason,workload_completed,workload_duration_ms,workload_exit_code,settings_changed,is_valid,skipped_samples,sampling_cpu_ms,sampling_wall_ms,sampling_allocated_bytes");
            // Empty/error sessions still export their explicit completion/validity metadata.
            foreach (var sample in result.Samples.Cast<SessionBenchmarkSample?>().DefaultIfEmpty(null)) csv.WriteLine(string.Join(",", new object?[] {
                sample?.Time.ToString("O", CultureInfo.InvariantCulture), sample?.ElapsedMs, sample?.System.Available, sample?.System.Commit,
                sample?.System.CpuPercent, sample?.System.PagesInput, sample?.UiProcess?.Pid, sample?.UiProcess?.CpuPercent,
                sample?.UiProcess?.WorkingSetBytes, sample?.UiProcess?.PrivateBytes, sample?.EngineProcess?.Pid,
                sample?.EngineProcess?.CpuPercent, sample?.EngineProcess?.WorkingSetBytes, sample?.EngineProcess?.PrivateBytes,
                result.StopReason, result.WorkloadCompleted, result.WorkloadDurationMs, result.WorkloadExitCode,
                result.SettingsChanged, result.IsValid, result.SkippedSamples, result.SamplingCpuMs, result.SamplingWallMs, result.SamplingAllocatedBytes
            }.Select(x => x is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : x?.ToString() ?? "")));
        });
    }
}

// Injectable clock/observer keeps regression tests fast without changing production sampling limits.
internal abstract class BenchmarkSource : IDisposable
{
    internal abstract DateTimeOffset UtcNow { get; }
    internal abstract TimeSpan Elapsed { get; }
    internal abstract Settings ReadSettings();
    internal abstract SessionBenchmarkSample Read();
    internal abstract void Wait(WaitHandle stop, TimeSpan delay);
    internal virtual BenchmarkWorkload Launch(ProcessStartInfo info) => new NativeBenchmarkWorkload(info);
    public virtual void Dispose() { }
}

internal abstract class BenchmarkWorkload : IDisposable
{
    internal abstract DateTimeOffset? ExitTime { get; }
    internal abstract double DurationMs { get; }
    internal abstract int ExitCode { get; }
    public abstract void Dispose();
}

internal sealed class NativeBenchmarkWorkload : BenchmarkWorkload
{
    private readonly Process process;
    private readonly DateTimeOffset launched = DateTimeOffset.UtcNow;
    internal NativeBenchmarkWorkload(ProcessStartInfo info) => process = Process.Start(info) ?? throw new IOException("Could not launch workload.");
    // ExitTime is the OS-recorded exit timestamp, not the later polling timestamp.
    internal override DateTimeOffset? ExitTime => process.HasExited ? new DateTimeOffset(process.ExitTime.ToUniversalTime()) : null;
    internal override double DurationMs => Math.Max(0, (ExitTime!.Value - launched).TotalMilliseconds);
    internal override int ExitCode => process.ExitCode;
    public override void Dispose() => process.Dispose(); // Never terminate user workloads.
}

internal sealed class NativeBenchmarkSource : BenchmarkSource
{
    private readonly Engine engine;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Process ui;
    private readonly Process? engineProcess;
    private long idle, kernel, user;
    private double? uiCpu, engineCpu;
    private double uiSeconds, engineSeconds;
    private IntPtr query, input;
    internal NativeBenchmarkSource(Engine engine)
    {
        this.engine = engine;
        ui = Process.GetCurrentProcess();
        try {
            // Full engine state/IPC is read once, never in the sampling loop.
            int pid = engine.GetState().EnginePid;
            if (pid != ui.Id) engineProcess = Process.GetProcessById(pid);
            if (!WindowsNative.GetSystemTimes(out idle, out kernel, out user)) throw new IOException("System CPU telemetry unavailable.");
            if (WindowsNative.PdhOpenQuery(null, 0, out query) == 0 &&
                WindowsNative.PdhAddEnglishCounter(query, @"\Memory\Pages Input/sec", 0, out input) == 0)
                WindowsNative.PdhCollectQueryData(query);
        } catch { Dispose(); throw; }
    }
    internal override DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    internal override TimeSpan Elapsed => clock.Elapsed;
    internal override Settings ReadSettings() => engine.Settings;
    internal override void Wait(WaitHandle stop, TimeSpan delay) => stop.WaitOne(delay);
    internal override SessionBenchmarkSample Read()
    {
        var memory = new WindowsNative.Memory { Length = (uint)Marshal.SizeOf<WindowsNative.Memory>() };
        var perf = new WindowsNative.Performance { Size = (uint)Marshal.SizeOf<WindowsNative.Performance>() };
        if (!WindowsNative.GlobalMemoryStatusEx(ref memory) || !WindowsNative.GetPerformanceInfo(ref perf, perf.Size) ||
            !WindowsNative.GetSystemTimes(out long nextIdle, out long nextKernel, out long nextUser)) throw new IOException("System telemetry unavailable.");
        long total = nextKernel - kernel + nextUser - user;
        double cpu = total > 0 ? Math.Clamp(100 * (1 - (double)(nextIdle - idle) / total), 0, 100) : 0;
        (idle, kernel, user) = (nextIdle, nextKernel, nextUser);
        var snap = new Snapshot { Time = UtcNow, RamTotal = memory.Total, Available = memory.Available,
            Commit = (ulong)perf.Commit * (ulong)perf.PageSize, CommitLimit = (ulong)perf.CommitLimit * (ulong)perf.PageSize,
            CpuPercent = cpu };
        // Only the cheap memory/CPU pressure probe runs while the host is busy.
        if (BenchmarkSession.UnderPressure(snap)) return new(snap.Time, clock.Elapsed.TotalMilliseconds, snap, null, null);
        double? pages = null;
        if (input != IntPtr.Zero && WindowsNative.PdhCollectQueryData(query) == 0 &&
            WindowsNative.PdhGetFormattedCounterValue(input, 0x200, out _, out var value) == 0 && value.Status <= 1 && double.IsFinite(value.Value)) pages = Math.Max(0, value.Value);
        snap = snap with { PagesInput = pages };
        double seconds = clock.Elapsed.TotalSeconds;
        var uiMetrics = ReadProcess(ui, seconds, ref uiCpu, ref uiSeconds);
        var engineMetrics = engineProcess is null ? uiMetrics : ReadProcess(engineProcess, seconds, ref engineCpu, ref engineSeconds);
        return new(snap.Time, clock.Elapsed.TotalMilliseconds, snap, uiMetrics, engineMetrics);
    }
    private SessionProcessMetrics? ReadProcess(Process process, double seconds, ref double? previousCpu, ref double previousSeconds)
    {
        try {
            process.Refresh();
            double cpu = process.TotalProcessorTime.TotalSeconds;
            double? percent = previousCpu.HasValue && seconds > previousSeconds ? Math.Clamp((cpu - previousCpu.Value) / (seconds - previousSeconds) / Environment.ProcessorCount * 100, 0, 100) : null;
            var result = new SessionProcessMetrics(process.Id, percent, process.WorkingSet64, process.PrivateMemorySize64);
            previousCpu = cpu; previousSeconds = seconds;
            return result;
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }
    public override void Dispose()
    {
        if (query != IntPtr.Zero) { WindowsNative.PdhCloseQuery(query); query = IntPtr.Zero; }
        engineProcess?.Dispose(); ui.Dispose();
    }
}
