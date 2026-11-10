using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ResearchLab;

public sealed class IndependentObserver : IAsyncDisposable
{
    readonly Process child;
    readonly long identity;
    readonly string phaseFile, output;
    readonly object gate = new();
    readonly StringBuilder errors = new();
    readonly Task stderr;
    Task stdout = Task.CompletedTask;
    Task? stopping;
    long revision;
    bool exited;

    IndependentObserver(Process child, long identity, string phaseFile, string output)
    {
        this.child = child; this.identity = identity; this.phaseFile = phaseFile; this.output = output;
        stderr = Drain(child.StandardError, errors);
    }

    public static async Task<IndependentObserver> StartAsync(string executable, string directory,
        IReadOnlyDictionary<string, (int Pid, long Identity)> targets)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Observer requires Windows.");
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > 32) throw new ArgumentException("At most 32 explicit targets are allowed.");
        var list = targets.Take(33).Select(x => new ObserverTarget(x.Key, x.Value.Pid, x.Value.Identity)).ToArray();
        ObserverProtocol.ValidateTargets(list);
        string exe = Path.GetFullPath(executable), root = Path.GetFullPath(directory);
        if (!File.Exists(exe)) throw new FileNotFoundException("Observer apphost not found.", exe);
        Directory.CreateDirectory(root);
        string stem = Path.Combine(root, "observer-" + Guid.NewGuid().ToString("N"));
        string targetFile = stem + ".targets.json", phaseFile = stem + ".phase.json", output = stem + ".raw.jsonl";
        using (var file = new FileStream(targetFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, list); file.Flush(true); }
        ObserverProtocol.WritePhase(phaseFile, new("idle", 0));
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(exe)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "observer", "--targets", targetFile, "--phase-file", phaseFile, "--output", output,
            "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--parent-identity", parent.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new IOException("Observer failed to launch.");
        // Keep the handle returned by Start; cleanup never reacquires a reused PID.
        long created;
        try { created = ObserverNative.Creation(process.SafeHandle); }
        catch (Exception capture)
        {
            try
            {
                ObserverProtocol.WritePhase(phaseFile, new("stop", 1));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception cleanup) { throw new AggregateException("Cannot capture helper identity or confirm graceful cleanup.", capture, cleanup); }
            finally { process.Dispose(); }
            throw;
        }
        var observer = new IndependentObserver(process, created, phaseFile, output);
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            observer.stdout = Drain(process.StandardOutput, null);
            if (ready != "ready") throw new IOException("Observer did not send ready.");
            if (process.HasExited) throw new IOException("Observer exited during startup.");
            return observer;
        }
        catch (Exception startup)
        {
            try { await observer.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Observer startup and cleanup failed.", startup, cleanup); }
            throw;
        }
    }

    public void SetPhase(string phase)
    {
        ObserverProtocol.ValidatePhase(phase, allowStop: false);
        lock (gate)
        {
            if (stopping is not null) throw new ObjectDisposedException(nameof(IndependentObserver));
            ObserverProtocol.WritePhase(phaseFile, new(phase, checked(++revision)));
        }
    }

    public Task StopAsync()
    {
        lock (gate) return stopping ??= StopCore();
    }

    async Task StopCore()
    {
        Exception? failure = null;
        try { ObserverProtocol.WritePhase(phaseFile, new("stop", checked(++revision))); }
        catch (Exception e) { failure = e; }
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            ObserverNative.TerminateOwned(child.SafeHandle, child.Id, identity);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            failure = new IOException("Observer required termination; preserve and inspect its raw output.", failure);
        }
        exited = true; // Process exit closes its writer, including after forced termination.
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
        if (child.ExitCode != 0) failure = new IOException($"Observer exited {child.ExitCode}: {errors}", failure);
        if (failure is not null) throw failure;
    }

    public JsonElement[] ReadSamples()
    {
        lock (gate)
        {
            if (stopping is null || !stopping.IsCompleted || !exited)
                throw new InvalidOperationException("Await StopAsync before reading observer samples.");
        }
        var rows = new List<JsonElement>();
        foreach (string line in File.ReadLines(output))
        {
            // A malformed/truncated record is a dataset failure, never silently discarded.
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid observer row.");
            rows.Add(document.RootElement.Clone());
        }
        return rows.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { child.Dispose(); }
    }

    static async Task Drain(StreamReader reader, StringBuilder? tail)
    {
        var buffer = new char[512];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
            if (tail is not null) { tail.Append(buffer, 0, read); if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096); }
    }
}

public static class ObserverWorker
{
    sealed record Previous(ulong Cpu, long Stamp, ObserverPhase Phase, uint Cores);
    sealed record Reading(ObserverTarget Target, ObserverMeasurement? Value, string? Error);

    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Observer requires Windows.");
            var options = Parse(args);
            string targetsPath = Path.GetFullPath(options["--targets"]), phasePath = Path.GetFullPath(options["--phase-file"]),
                output = Path.GetFullPath(options["--output"]);
            if (new[] { targetsPath, phasePath, output }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
                throw new ArgumentException("Observer data and control paths must be distinct.");
            if (!int.TryParse(options["--parent-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid) || parentPid <= 0 ||
                !long.TryParse(options["--parent-identity"], NumberStyles.None, CultureInfo.InvariantCulture, out long parentIdentity) || parentIdentity <= 0)
                throw new ArgumentException("Parent PID and creation identity must be positive.");
            var targets = ObserverProtocol.ReadTargets(targetsPath).ToList();
            using var self = Process.GetCurrentProcess();
            if (targets.Any(x => x.pid == Environment.ProcessId)) throw new ArgumentException("Helper cannot be an explicit target.");
            targets.Add(new("observer", Environment.ProcessId, ObserverNative.Creation(self.SafeHandle)));
            using var raw = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };
            var previous = new Dictionary<string, Previous>(StringComparer.Ordinal);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            bool ready = false;
            while (true)
            {
                ObserverPhase before;
                try { before = ObserverProtocol.ReadPhase(phasePath); }
                catch (Exception e) { Fail(null, "phase_unavailable: " + e.Message); return 3; }
                if (before.phase == "stop") return 0;
                if (!ObserverNative.Alive(parentPid, parentIdentity))
                { Fail(before.phase, "parent_exited_unavailable_or_identity_mismatch"); return 0; }
                if (!ready) { await Console.Out.WriteLineAsync("ready"); await Console.Out.FlushAsync(); ready = true; }
                uint cores;
                try { cores = ObserverNative.LogicalCores(); }
                catch (Exception e) { Fail(before.phase, "logical_cpu_count_unavailable: " + e.Message); return 3; }
                var readings = new List<Reading>();
                foreach (var target in targets)
                {
                    try { readings.Add(new(target, ObserverNative.Measure(target), null)); }
                    catch (Exception e) { readings.Add(new(target, null, e.Message)); }
                }
                ObserverPhase after;
                try { after = ObserverProtocol.ReadPhase(phasePath); }
                catch (Exception e) { Fail(null, "phase_unavailable: " + e.Message); return 3; }
                if (after.phase == "stop") return 0;
                bool mixed = before != after;
                foreach (var reading in readings)
                {
                    var target = reading.Target;
                    double? percent = null;
                    string cpuStatus = "first";
                    if (reading.Value is not { } value)
                    {
                        previous.Remove(target.role);
                        Write(target, after.phase, null, null, null, DateTimeOffset.UtcNow, "unavailable", reading.Error, "unavailable");
                        continue;
                    }
                    if (mixed) { previous.Remove(target.role); cpuStatus = "mixed_phase"; }
                    else
                    {
                        if (previous.TryGetValue(target.role, out var prior))
                        {
                            cpuStatus = "mixed_phase";
                            if (prior.Phase == after && prior.Cores == cores && value.Stamp > prior.Stamp && value.Cpu >= prior.Cpu)
                            {
                                percent = (value.Cpu - prior.Cpu) / 10_000_000.0 /
                                    ((value.Stamp - prior.Stamp) / (double)Stopwatch.Frequency) / cores * 100;
                                cpuStatus = "ok";
                            }
                        }
                        previous[target.role] = new(value.Cpu, value.Stamp, after, cores);
                    }
                    Write(target, after.phase, percent, value.PrivateBytes, value.WorkingSet, value.Time, "ok", null, cpuStatus);
                }
                if (!await timer.WaitForNextTickAsync()) return 0;
            }

            void Fail(string? phase, string error)
            { foreach (var target in targets) Write(target, phase, null, null, null, DateTimeOffset.UtcNow, "unavailable", error, "unavailable"); }
            void Write(ObserverTarget target, string? phase, double? cpu, ulong? privateBytes, ulong? workingSet,
                DateTimeOffset time, string status, string? error, string cpuStatus) => raw.WriteLine(JsonSerializer.Serialize(new
                {
                    role = target.role, phase, cpu_percent = cpu, private_bytes = privateBytes, working_set_bytes = workingSet,
                    decision_ms = (double?)null, log_write_ms = (double?)null, time_utc = time,
                    pid = target.pid, creation_filetime = target.creation_filetime, status, error, cpu_status = cpuStatus
                }));
        }
        catch (Exception e) { await Console.Error.WriteLineAsync($"Observer failed: {e.GetType().Name}: {e.Message}"); return 2; }
    }

    static Dictionary<string, string> Parse(string[] args)
    {
        string[] allowed = ["--targets", "--phase-file", "--output", "--parent-pid", "--parent-identity"];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = args.Length > 0 && args[0] == "observer" ? 1 : 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !result.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Invalid, duplicate or incomplete observer option.");
        }
        if (result.Count != allowed.Length) throw new ArgumentException("All five observer options are required.");
        return result;
    }
}

internal sealed record ObserverTarget(string role, int pid, long creation_filetime);
internal sealed record ObserverPhase(string phase, long revision);
internal sealed record ObserverMeasurement(ulong Cpu, long Stamp, DateTimeOffset Time, ulong PrivateBytes, ulong WorkingSet);

internal static class ObserverProtocol
{
    internal static void ValidateTargets(IReadOnlyList<ObserverTarget> targets)
    {
        if (targets.Count > 32) throw new ArgumentException("At most 32 explicit targets are allowed.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var pids = new HashSet<int>();
        foreach (var target in targets)
            if (string.IsNullOrWhiteSpace(target.role) || target.role.Length > 64 || target.role.Any(char.IsControl) ||
                target.role.Equals("observer", StringComparison.OrdinalIgnoreCase) || !roles.Add(target.role) ||
                target.pid <= 0 || target.creation_filetime <= 0 || !pids.Add(target.pid))
                throw new ArgumentException("Targets require unique roles/PIDs, positive PID/creation identity; observer is reserved.");
    }

    internal static void ValidatePhase(string phase, bool allowStop)
    {
        if (phase is not ("idle" or "observe" or "evaluate" or "intervene" or "restore") && !(allowStop && phase == "stop"))
            throw new ArgumentException("Invalid observer phase.");
    }

    internal static ObserverTarget[] ReadTargets(string path)
    {
        using var doc = ReadJson(path, 65536);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 32)
            throw new InvalidDataException("Targets must be an array of at most 32 objects.");
        var list = new List<ObserverTarget>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            ExactProperties(row, "role", "pid", "creation_filetime");
            list.Add(new(row.GetProperty("role").GetString()!, row.GetProperty("pid").GetInt32(), row.GetProperty("creation_filetime").GetInt64()));
        }
        ValidateTargets(list);
        return list.ToArray();
    }

    internal static ObserverPhase ReadPhase(string path)
    {
        using var doc = ReadJson(path, 4096);
        ExactProperties(doc.RootElement, "phase", "revision");
        var value = new ObserverPhase(doc.RootElement.GetProperty("phase").GetString()!, doc.RootElement.GetProperty("revision").GetInt64());
        ValidatePhase(value.phase, allowStop: true);
        if (value.revision < 0) throw new InvalidDataException("Phase revision must be nonnegative.");
        return value;
    }

    static JsonDocument ReadJson(string path, int limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is <= 0 || file.Length > limit) throw new InvalidDataException("Observer control file exceeds its size bound or is empty.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Control file changed while reading.");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    }

    static void ExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a control object.");
        var found = element.EnumerateObject().Select(x => x.Name).ToArray();
        if (found.Length != names.Length || found.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Except(found).Any())
            throw new InvalidDataException("Invalid or duplicate control properties.");
    }

    internal static void WritePhase(string path, ObserverPhase phase)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, phase); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class ObserverNative
{
    const uint Query = 0x1000, Synchronize = 0x100000, Timeout = 258;
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle process,
        out long creation, out long exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(SafeProcessHandle process, uint code);

    internal static long Creation(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out long creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return creation;
    }

    internal static bool Alive(int pid, long identity)
    {
        using var handle = OpenProcess(Query | Synchronize, false, pid);
        return !handle.IsInvalid && GetProcessTimes(handle, out long created, out _, out _, out _) &&
            created == identity && WaitForSingleObject(handle, 0) == Timeout;
    }

    internal static uint LogicalCores()
    {
        uint count = GetActiveProcessorCount(ushort.MaxValue);
        return count > 0 ? count : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static ObserverMeasurement Measure(ObserverTarget target)
    {
        using var handle = OpenProcess(Query | Synchronize, false, target.pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetProcessTimes(handle, out long creation, out _, out ulong kernel, out ulong user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (creation != target.creation_filetime) throw new InvalidOperationException("creation_identity_mismatch");
        if (WaitForSingleObject(handle, 0) != Timeout) throw new InvalidOperationException("process_exited_or_unavailable");
        long stamp = Stopwatch.GetTimestamp(); var utc = DateTimeOffset.UtcNow;
        var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (!K32GetProcessMemoryInfo(handle, ref memory, memory.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (WaitForSingleObject(handle, 0) != Timeout) throw new InvalidOperationException("process_exited_during_sample");
        return new(checked(kernel + user), stamp, utc, memory.PrivateUsage.ToUInt64(), memory.WorkingSetSize.ToUInt64());
    }

    internal static void TerminateOwned(SafeProcessHandle handle, int pid, long identity)
    {
        if (GetProcessId(handle) != (uint)pid || Creation(handle) != identity)
            throw new InvalidOperationException("Refusing to terminate a different process identity.");
        if (WaitForSingleObject(handle, 0) == Timeout && !TerminateProcess(handle, 3))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
