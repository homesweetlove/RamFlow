using System.Text.Json;

namespace RamFlow.Core;

public sealed class Engine : IDisposable
{
    private sealed record Original(ProcessSample Process, ResourceValues Values);
    private readonly object gate = new();
    private readonly IResourceProvider provider;
    private readonly string root;
    private readonly bool persist;
    private Settings settings = new();
    private readonly Pressure pressure = new();
    private readonly Dictionary<int, Original> originals = new();
    private readonly HashSet<int> pending = new();
    private readonly Dictionary<string, DateTimeOffset> activity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int, long), DateTimeOffset> seen = new();
    private readonly Dictionary<int, DateTimeOffset> trims = new();
    private readonly Dictionary<int, DateTimeOffset> failures = new();
    private readonly Dictionary<string, int> learning = new();
    private readonly Queue<LogEntry> events = new();
    private IReadOnlyList<ProcessSample> processes = [];
    private State state = new(new(), 0, PressureLevel.Normal, "background", "", [], [], 0, [], []);
    private DateTimeOffset lastScan = DateTimeOffset.MinValue, lastTrim = DateTimeOffset.MinValue;
    private Task? worker;
    private CancellationTokenSource? cancellation;
    private bool disposed;
    private bool remote;
    private string lastForeground = "";
    public Engine(IResourceProvider provider, string? dataRoot = null, bool persist = true, bool allowRemote = true)
    {
        this.provider = provider; this.persist = persist;
        root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamFlowNative");
        if (persist && allowRemote && provider is WindowsResourceProvider) {
            try { remote = EnginePipe.Request<bool>(new("ping"), 250); } catch { }
            string helper = Path.Combine(AppContext.BaseDirectory, "RamFlow.Service.exe");
            if (!remote && File.Exists(helper)) {
                var info = new System.Diagnostics.ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("--serve"); info.ArgumentList.Add("--data-root"); info.ArgumentList.Add(root);
                using var child = System.Diagnostics.Process.Start(info);
                for (int attempt = 0; attempt < 40 && !remote; attempt++) { try { remote = EnginePipe.Request<bool>(new("ping"), 200); } catch { Thread.Sleep(50); } }
                if (!remote) throw new IOException("분리 엔진 시작 실패. 로그를 확인하세요.");
            }
        }
        if (persist) {
            Directory.CreateDirectory(root);
            try { settings = Validate(JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json"))) ?? new()); } catch { settings = new(); }
            try { foreach (var item in JsonSerializer.Deserialize<List<Original>>(File.ReadAllText(Path.Combine(root, "restore-journal.json"))) ?? []) {
                if (!SafeValues(item.Values) || item.Process.Pid <= 4) continue;
                originals[item.Process.Pid] = item; pending.Add(item.Process.Pid);
            } } catch { }
            try { foreach (var item in JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(root, "usage.json"))) ?? []) learning[item.Key] = item.Value; } catch { }
        }
    }
    public Settings Settings { get { if (remote) return EnginePipe.Request<Settings>(new("settings")); lock (gate) return settings with { Whitelist = settings.Whitelist.ToArray() }; } }
    public State GetState() { if (remote) return EnginePipe.Request<State>(new("state")); lock (gate) return state with { Events = events.ToArray(), PendingRestores = pending.Count }; }
    public void ApplySettings(Settings next)
    {
        if (remote) { EnginePipe.Request<Settings>(new("apply", Validate(next))); return; }
        lock (gate) {
            next = Validate(next);
            RestoreAll(); settings = next with { Whitelist = next.Whitelist.ToArray() };
            lastScan = DateTimeOffset.MinValue;
            if (persist) Save("settings.json", settings);
            Log("설정 변경 · 원래 자원 설정 복원 요청", next.DryRun);
        }
    }
    public static Settings Validate(Settings value)
    {
        if (!Display.Modes.Contains(value.Mode) || value.Whitelist is null || value.Whitelist.Length > 256 || value.Whitelist.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 260)) throw new ArgumentException("설정 모드 또는 예외 목록이 올바르지 않습니다.");
        return value;
    }
    public void Start()
    {
        if (remote) return;
        lock (gate) {
            if (worker is { IsCompleted: false }) return;
            cancellation = new(); var token = cancellation.Token;
            worker = Task.Run(async () => {
                DateTimeOffset next = DateTimeOffset.MinValue;
                while (!token.IsCancellationRequested) {
                    try {
                        if (DateTimeOffset.UtcNow >= next) { Tick(); next = DateTimeOffset.UtcNow.AddSeconds(2); }
                        else RestoreForeground();
                    } catch (Exception e) { lock (gate) Log("모니터링 오류: " + e.Message); }
                    try { await Task.Delay(250, token); } catch (OperationCanceledException) { break; }
                }
            }, token);
        }
    }
    public State Tick()
    {
        lock (gate) {
            foreach (int pid in originals.Keys.ToArray()) {
                var identity = provider.Identity(pid);
                if (identity is not null && identity != originals[pid].Process.Identity || identity is null && provider.Exited(pid)) { originals.Remove(pid); pending.Remove(pid); }
            }
            foreach (int pid in pending.ToArray()) Restore(pid);
            if (settings.Paused || settings.DryRun) RestoreAll();
            var snap = provider.Sample(); var now = snap.Time;
            var foreground = provider.Foreground(); bool fullscreen = provider.Fullscreen();
            if (foreground.Name.Length > 0) activity[foreground.Name] = now;
            if (foreground.Name != lastForeground) {
                Log($"현재 앱: {foreground.Name}"); lastForeground = foreground.Name;
                if (settings.Learning && foreground.Name.Length > 0) {
                    string key = $"{now.ToLocalTime().Hour:D2}|{foreground.Name}";
                    learning[key] = Math.Min(10000, learning.GetValueOrDefault(key) + 1);
                    if (persist) Save("usage.json", learning);
                }
            }
            if ((now - lastScan).TotalSeconds >= 10) { processes = provider.Processes(); lastScan = now; }
            string mode = Policy.Mode(settings, foreground.Name, snap, fullscreen);
            // 학습은 예측 앱을 보호하며 압박 임계값을 낮추는 선제 정리는 하지 않는다.
            var predictions = settings.Learning ? learning.Where(x => x.Key.StartsWith($"{now.ToLocalTime().Hour:D2}|") && x.Value >= 3).OrderByDescending(x => x.Value).Take(3).Select(x => x.Key.Split('|')[1]).ToArray() : [];
            pressure.Update(snap, snap.RamTotal >= 32UL * 1073741824 ? -5 : snap.RamTotal <= 8UL * 1073741824 ? 2 : 0);
            var rows = new List<ProcessRow>();
            var alive = new HashSet<(int, long)>();
            foreach (var p in processes) {
                var key = (p.Pid, p.Identity); alive.Add(key);
                if (!seen.ContainsKey(key)) { seen[key] = now; activity[p.Name] = now; }
                if (p.CpuPercent >= 2 || p.IoBytesPerSecond >= 1048576) activity[p.Name] = now;
                double idle = (now - activity.GetValueOrDefault(p.Name, now)).TotalSeconds;
                string category = p.Name == foreground.Name ? "Active" : idle < 300 ? "Recently Active" : idle < 1800 ? "Background" : idle < 7200 ? "Idle" : "Deep Idle";
                string? protection = Policy.Protect(p, foreground, settings, mode);
                if (protection is null && predictions.Contains(p.Name)) protection = "시간대 사용 예측 보호";
                rows.Add(new(p.Pid, p.Identity, p.Name, p.WorkingSet, p.PrivateBytes, category, protection, p.CpuPercent, p.IoBytesPerSecond));
                bool eligible = protection is null && idle >= 1800 && !fullscreen && !settings.Paused &&
                    (pressure.Level >= PressureLevel.Moderate || snap.CpuPercent >= 80 || snap.OnBattery);
                if (!eligible) { if (originals.ContainsKey(p.Pid)) Restore(p.Pid); continue; }
                if (settings.DryRun) { Log($"{p.Name} · {category} · Memory Priority / CPU / EcoQoS 조정 예상", true); continue; }
                if (!originals.ContainsKey(p.Pid) && !pending.Contains(p.Pid) && (!failures.TryGetValue(p.Pid, out var failed) || (now - failed).TotalSeconds >= 60)) Apply(p, now);
                if (settings.WorkingSetTrim && pressure.Level >= PressureLevel.High && idle >= 7200 && p.WorkingSet >= 150 * 1048576UL &&
                    (now - lastTrim).TotalSeconds >= 120 && (now - trims.GetValueOrDefault(p.Pid, DateTimeOffset.MinValue)).TotalSeconds >= 900 && provider.Validate(p)) {
                    bool ok = provider.Trim(p.Pid, p.Identity); lastTrim = now; trims[p.Pid] = now;
                    Log($"{p.Name} · 제한적 Working Set 축소 {(ok ? "완료" : "실패")}");
                }
            }
            foreach (var key in seen.Keys.Except(alive).ToArray()) seen.Remove(key);
            foreach (var name in activity.Keys.Except(processes.Select(p => p.Name)).ToArray()) activity.Remove(name);
            if (persist) SaveJournal();
            state = new(snap, pressure.Score, pressure.Level, mode, foreground.Name, rows.OrderByDescending(x => x.WorkingSet).ToArray(), events.ToArray(), pending.Count, provider.Topology(), predictions);
            return state;
        }
    }
    private void Apply(ProcessSample process, DateTimeOffset now)
    {
        if (!provider.Validate(process)) return;
        var old = provider.ReadValues(process.Pid);
        if (old is null || old.CpuPriority is not (0x40 or 0x4000 or 0x20) || provider.Identity(process.Pid) != process.Identity) return;
        uint[] sets = old.CpuSets;
        if (settings.CpuSets) {
            var topology = provider.Topology(); var efficiency = topology.Where(x => !x.Parked && !x.Allocated).ToArray();
            if (efficiency.Select(x => x.Efficiency).Distinct().Count() > 1 && topology.Select(x => x.Group).Distinct().Count() == 1)
                sets = efficiency.Where(x => x.Efficiency == efficiency.Min(x => x.Efficiency)).Select(x => x.Id).ToArray();
        }
        var value = old with { MemoryPriority = Math.Min(old.MemoryPriority, 3), CpuPriority = settings.CpuManagement && old.CpuPriority != 0x40 ? 0x4000U : old.CpuPriority,
            PowerControl = settings.EcoQos ? old.PowerControl | 1U : old.PowerControl, PowerState = settings.EcoQos ? old.PowerState | 1U : old.PowerState, CpuSets = sets };
        originals[process.Pid] = new(process, old);
        // 일부 성공이나 강제 종료에 대비해 API 호출 전에 복원할 값을 보존한다.
        if (persist) SaveJournal();
        if (!provider.Validate(process)) { originals.Remove(process.Pid); return; }
        bool ok = provider.WriteValues(process.Pid, value, process.Identity);
        if (!ok) { pending.Add(process.Pid); failures[process.Pid] = now; }
        Log($"{process.Name} · 우선순위/EcoQoS/CPU Sets {(ok ? "적용" : "일부 실패 · 원복 재시도")}");
    }
    private void Restore(int pid)
    {
        if (!originals.TryGetValue(pid, out var original)) return;
        long? identity = provider.Identity(pid);
        if (identity is null) { if (provider.Exited(pid)) { originals.Remove(pid); pending.Remove(pid); } else pending.Add(pid); return; }
        if (identity != original.Process.Identity) { originals.Remove(pid); pending.Remove(pid); return; }
        if (!provider.Validate(original.Process, restoring: true) || !SafeValues(original.Values)) { pending.Add(pid); return; }
        if (provider.WriteValues(pid, original.Values, identity.Value)) { originals.Remove(pid); pending.Remove(pid); Log($"{original.Process.Name} · 원래 자원 설정 복원"); }
        else pending.Add(pid);
    }
    private static bool SafeValues(ResourceValues value) => value.MemoryPriority is >= 1 and <= 5 && value.CpuPriority is 0x20 or 0x4000 or 0x40 &&
        (value.PowerControl & ~5U) == 0 && (value.PowerState & ~5U) == 0 && value.CpuSets is { Length: <= 4096 };
    private void RestoreForeground()
    {
        lock (gate) {
            var fg = provider.Foreground();
            if (fg.Name.Length > 0) activity[fg.Name] = DateTimeOffset.UtcNow;
            foreach (var (pid, original) in originals.ToArray()) if (pid == fg.Pid || original.Process.Name.Equals(fg.Name, StringComparison.OrdinalIgnoreCase)) Restore(pid);
        }
    }
    private void RestoreAll() { foreach (int pid in originals.Keys.ToArray()) Restore(pid); if (persist) SaveJournal(); }
    private void SaveJournal() => Save("restore-journal.json", originals.Values.ToArray());
    private void Save<T>(string name, T value)
    {
        string path = Path.Combine(root, name), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void Log(string message, bool dry = false)
    {
        events.Enqueue(new(DateTimeOffset.UtcNow, message, dry)); while (events.Count > 300) events.Dequeue();
        if (persist && !dry) try { File.AppendAllText(Path.Combine(root, "events.jsonl"), JsonSerializer.Serialize(new LogEntry(DateTimeOffset.UtcNow, message, dry)) + "\n"); } catch (IOException) { }
    }
    public void Stop()
    {
        if (remote) { try { EnginePipe.Request<bool>(new("stop")); } catch { } return; }
        Task? task;
        lock (gate) { cancellation?.Cancel(); task = worker; }
        if (task is not null && Task.CurrentId != task.Id) try { task.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        lock (gate) { RestoreAll(); }
    }
    public void Dispose() { if (disposed) return; Stop(); provider.Dispose(); cancellation?.Dispose(); disposed = true; }
}
