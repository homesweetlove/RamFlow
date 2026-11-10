using System.Text.Json;

namespace RamFlow.Core;

public sealed class Engine : IDisposable
{
    private sealed record Original(ProcessSample Process, ResourceValues Values);
    private readonly object gate = new();
    private readonly IResourceProvider provider;
    private readonly string root;
    private readonly bool persist;
    private readonly BackgroundJobQueue jobs;
    private readonly IPolicyObservationSource? observations;
    private readonly IResearchAuditSink? audit;
    private IResourcePolicy resourcePolicy = new V1ResourcePolicy();
    private readonly Dictionary<(int, long), ResourceAction> appliedActions = new();
    private string settingsHash = FileResearchAuditSink.Hash(new Settings());
    private DateTimeOffset? policyTimestamp;
    private readonly System.Diagnostics.Stopwatch policyClock = new();
    private DateTimeOffset PolicyNow => policyTimestamp is {} time ? time + policyClock.Elapsed : DateTimeOffset.UtcNow;
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
    private int trimCount;
    private bool cpuPressure;
    private ulong lastWorkingSetReduction;
    private Task? worker;
    private CancellationTokenSource? cancellation;
    private bool disposed;
    private bool remote;
    private string lastForeground = "";
    public bool IsRemote => remote;
    public void Detach() {
        if(!remote)throw new InvalidOperationException("로컬 엔진 소유자는 복원 없이 분리할 수 없습니다.");
        if(disposed)return; jobs.Dispose();provider.Dispose();disposed=true;
    }
    public Engine(IResourceProvider provider, string? dataRoot = null, bool persist = true, bool allowRemote = true, IBackgroundJobLauncher? jobLauncher = null,
        IPolicyObservationSource? observations = null, IResearchAuditSink? audit = null)
    {
        this.provider = provider; this.persist = persist;
        this.observations = observations; this.audit = audit;
        if ((observations is not null || audit is not null) && provider is WindowsResourceProvider && (allowRemote || dataRoot is null))
            throw new ArgumentException("A research Windows host must use allowRemote:false and an isolated explicit dataRoot.");
        jobs = new(jobLauncher);
        root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamFlowNative");
        if (persist && allowRemote && provider is WindowsResourceProvider) {
            try { remote = EnginePipe.Request<bool>(new("ping"), 250); } catch { }
            string helper = Path.Combine(AppContext.BaseDirectory, "RamFlow.Service.exe");
            if (!remote && File.Exists(helper)) {
                var info = new System.Diagnostics.ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("--serve"); info.ArgumentList.Add("--data-root"); info.ArgumentList.Add(root);
                if (EnginePipe.VerificationNamespace is string scope) { info.ArgumentList.Add("--ipc-namespace"); info.ArgumentList.Add(scope); }
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
        resourcePolicy = CreatePolicy(settings);
        settingsHash = FileResearchAuditSink.Hash(settings);
    }
    private static Settings CopySettings(Settings value) => value with { Whitelist = value.Whitelist.ToArray(), BackgroundApps = value.BackgroundApps.ToArray() };
    public Settings Settings { get { if (remote) return EnginePipe.Request<Settings>(new("settings")); lock (gate) return CopySettings(settings); } }
    public State GetState() { if (remote) return EnginePipe.Request<State>(new("state")); lock (gate) return state with { Events = events.ToArray(), PendingRestores = pending.Count, BackgroundJobs = jobs.Summaries }; }
    public BackgroundJobSummary EnqueueBackgroundJob(BackgroundJobRequest request) {
        if(remote) return EnginePipe.Request<BackgroundJobSummary>(new("enqueue", Job:request));
        lock(gate) { if(settings.DryRun || settings.Paused) throw new InvalidOperationException("실제 관리 모드에서 일시정지를 해제한 뒤 실행하세요."); return jobs.Enqueue(request); }
    }
    public bool CancelBackgroundJob(Guid id) { if(remote)return EnginePipe.Request<bool>(new("cancel-job",JobId:id));lock(gate)return jobs.Cancel(id); }
    public void ApplySettings(Settings next)
    {
        if (remote) { EnginePipe.Request<Settings>(new("apply", Validate(next))); return; }
        lock (gate) {
            next = Validate(next);
            RestoreAll(); settings = CopySettings(next);
            resourcePolicy = CreatePolicy(settings); settingsHash = FileResearchAuditSink.Hash(settings);
            lastScan = DateTimeOffset.MinValue;
            if (persist) Save("settings.json", settings);
            Log("설정 변경 · 원래 자원 설정 복원 요청", next.DryRun);
        }
    }
    public static Settings Validate(Settings value)
    {
        if (value.PolicyName is not ("v1" or "carp" or "fixed") || value.Carp is null) throw new ArgumentException("Unknown research policy.");
        CarpOptions.Validate(value.Carp);
        if (!Display.Modes.Contains(value.Mode) || value.Whitelist is null || value.Whitelist.Length > 256 || value.Whitelist.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 260)) throw new ArgumentException("설정 모드 또는 예외 목록이 올바르지 않습니다.");
        if (value.BackgroundApps is null || value.BackgroundApps.Length > 256 || value.BackgroundApps.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32767 || x.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(x))) throw new ArgumentException("배경 작업은 실행 파일의 전체 경로로 지정하세요.");
        if(value.BackgroundConcurrency is <1 or >16 || value.BackgroundReserveMiB is <256 or >65536) throw new ArgumentException("동시 실행은 1–16개, 메모리 여유는 256–65536 MiB로 지정하세요.");
        return value;
    }
    private static IResourcePolicy CreatePolicy(Settings value) => value.PolicyName switch {
        "carp" => new CarpPolicy(value.Carp), "fixed" => new FixedResourcePolicy(), _ => new V1ResourcePolicy()
    };
    private bool Audit(string kind, DateTimeOffset time, ProcessSample? process, string result, object? detail = null) {
        if (audit is null) return true;
        try { audit.Write(new(kind, time, resourcePolicy.Name, resourcePolicy.Version, settingsHash,
            process?.Pid, process?.Identity, result, detail)); return true; }
        catch (Exception e) {
            // Audit plugins and their fallback reporting must never prevent
            // foreground restoration, including under permission/disk failures.
            try { Log("연구 로그 기록 실패: " + e.Message); } catch (Exception) { }
            return false;
        }
    }
    public void Start()
    {
        if (remote) return;
        lock (gate) {
            if (worker is { IsCompleted: false }) return;
            jobs.Start();
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
                if (identity is not null && identity != originals[pid].Process.Identity || identity is null && provider.Exited(pid)) {
                    var previous = originals[pid].Process;
                    Audit("restore", DateTimeOffset.UtcNow, previous, identity is null ? "process-exited" : "pid-reused-no-write");
                    originals.Remove(pid); pending.Remove(pid); appliedActions.Remove((pid, previous.Identity));
                }
            }
            foreach (int pid in pending.ToArray()) Restore(pid);
            if (settings.Paused || settings.DryRun) RestoreAll();
            var snap = provider.Sample(); var now = snap.Time;
            policyTimestamp = now; policyClock.Restart();
            var tickClock = System.Diagnostics.Stopwatch.StartNew();
            cpuPressure = snap.CpuPercent >= 80 || cpuPressure && snap.CpuPercent >= 60;
            var count = jobs.Summaries.Count(x=>x.Status=="Running");
            // Queue admission is a conservative safety guard, not an estimated
            // foreground benefit. Running jobs are never suspended or killed.
            bool deferStarts = settings.PolicyName == "carp" && (cpuPressure || snap.CommitLimit == 0 || snap.Available < (ulong)settings.BackgroundReserveMiB * 1048576);
            bool queueRecorded = Audit("queue-admission", now, null, deferStarts ? "defer-new-starts" : "normal-reserve-gates", new { CpuPressure = cpuPressure, Snapshot = snap, Jobs = jobs.Summaries });
            jobs.Tick(snap,settings, deferStarts || !queueRecorded);
            if(jobs.Summaries.Count(x=>x.Status=="Running")!=count) lastScan=DateTimeOffset.MinValue;
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
            int trimDelay = Policy.TrimIdleSeconds(pressure.Level);
            bool needsMemoryRelief = Policy.NeedsWorkingSetRelief(snap, pressure.Level);
            int trimCandidates = 0;
            bool trimAttempted = false;
            double? nextIdleSeconds = null;
            bool canManage = foreground.Pid > 0 && foreground.Name.Length > 0 && !fullscreen && !settings.Paused;
            // Account for every sibling before selecting an app: a small busy helper
            // must protect a larger quiet process with the same executable name.
            foreach (var p in processes) {
                var key = (p.Pid, p.Identity); alive.Add(key);
                if (!seen.ContainsKey(key)) { seen[key] = now; activity[p.Name] = now; }
                if (!p.ActivityKnown || p.CpuPercent >= 2 || p.IoBytesPerSecond >= 1048576) activity[p.Name] = now;
            }
            foreach (var p in processes.OrderByDescending(p => p.WorkingSet)) {
                double idle = (now - activity.GetValueOrDefault(p.Name, now)).TotalSeconds;
                string category = p.Name == foreground.Name ? "Active" : idle < 300 ? "Recently Active" : idle < 1800 ? "Background" : idle < 7200 ? "Idle" : "Deep Idle";
                string? protection = Policy.Protect(p, foreground, settings, mode);
                if (protection is null && predictions.Contains(p.Name)) protection = "시간대 사용 예측 보호";
                rows.Add(new(p.Pid, p.Identity, p.Name, p.WorkingSet, p.PrivateBytes, category, protection, p.CpuPercent, p.IoBytesPerSecond));
                bool ownedJob = jobs.Owns(p);
                bool designated = ownedJob || settings.BackgroundApps.Contains(p.Path, StringComparer.OrdinalIgnoreCase);
                string? priorityProtection = designated ? Policy.Protect(p, foreground, settings, mode, true) : protection;
                CarpEvidence? evidence = null;
                if (settings.PolicyName == "carp" && designated && priorityProtection is null) {
                    try { evidence = observations?.Read(p, snap); }
                    catch (Exception e) { Audit("observation", now, p, "source-failed", new { Error = e.Message }); }
                }
                var input = new PolicyInput(snap, p, settings, foreground.Pid, foreground.Name, fullscreen, mode,
                    pressure.Level, cpuPressure, idle, designated, ownedJob, appliedActions.GetValueOrDefault((p.Pid, p.Identity)), evidence);
                PolicyDecision decision;
                try { decision = resourcePolicy.Decide(input); }
                catch (Exception e) { decision = new(ResourceAction.None, "policy-failed-restore", ContentionKind.Unknown, []); Audit("policy-error", now, p, "failed", new { Error = e.Message }); }
                // Learned future-app protection stays outside the swappable policy.
                if (!designated && protection is not null) decision = decision with { Action = ResourceAction.None, Reason = protection };
                bool recorded = Audit("decision", now, p, decision.Reason, new { Input = input, Decision = decision, DryRun = settings.DryRun });
                if (decision.Action == ResourceAction.None || !recorded) { if (originals.ContainsKey(p.Pid)) Restore(p.Pid); }
                else if (settings.DryRun) Log($"{p.Name} · {resourcePolicy.Name} · {decision.Action} 조정 예상", true);
                else {
                    if (settings.PolicyName != "v1" && originals.ContainsKey(p.Pid) && input.CurrentAction != decision.Action) Restore(p.Pid);
                    if (!originals.ContainsKey(p.Pid) && !pending.Contains(p.Pid) && (!failures.TryGetValue(p.Pid, out var failed) || (now - failed).TotalSeconds >= 60)) Apply(p, snap, decision.Action, designated, evidence, tickClock);
                }
                bool memoryCandidate = settings.PolicyName == "v1" && protection is null && canManage && settings.WorkingSetTrim &&
                    needsMemoryRelief && p.WorkingSet >= 150 * 1048576UL;
                if (!memoryCandidate) continue;
                if (idle < trimDelay) { nextIdleSeconds = Math.Min(nextIdleSeconds ?? double.MaxValue, trimDelay - idle); continue; }
                trimCandidates++;
                if (trimAttempted || (now - lastTrim).TotalSeconds < 120 || (now - trims.GetValueOrDefault(p.Pid, DateTimeOffset.MinValue)).TotalSeconds < 900) continue;
                if (settings.DryRun) continue;
                // Recheck activity immediately before an irreversible working-set eviction.
                var latestForeground = provider.Foreground();
                bool latestFullscreen = provider.Fullscreen();
                string latestMode = Policy.Mode(settings, latestForeground.Name, snap, latestFullscreen);
                if (latestForeground.Pid <= 0 || latestForeground.Name.Length == 0 || latestFullscreen || Policy.Protect(p, latestForeground, settings, latestMode) is not null || !provider.Validate(p)) continue;
                ulong? before = provider.WorkingSet(p.Pid, p.Identity);
                bool ok = provider.Trim(p.Pid, p.Identity); lastTrim = now; trims[p.Pid] = now;
                trimAttempted = true;
                ulong? after = ok ? provider.WorkingSet(p.Pid, p.Identity) : null;
                if (ok) {
                    trimCount++;
                    lastWorkingSetReduction = before.HasValue && after.HasValue && before.Value > after.Value ? before.Value - after.Value : 0;
                    Log($"{p.Name} · 제한적 Working Set 축소 완료 · 프로세스 상주량 {(before.HasValue && after.HasValue ? Display.Bytes(lastWorkingSetReduction) + " 감소" : "측정 불가")}");
                } else Log($"{p.Name} · 제한적 Working Set 축소 실패");
            }
            foreach (var key in seen.Keys.Except(alive).ToArray()) seen.Remove(key);
            foreach (var name in activity.Keys.Except(processes.Select(p => p.Name)).ToArray()) activity.Remove(name);
            if (persist) SaveJournal();
            string memoryStatus = settings.Paused ? "메모리 관리 일시정지" : settings.PolicyName != "v1" ? "현재 정책은 강제 RAM 정리를 사용하지 않음" : !settings.WorkingSetTrim ? "RAM 정리 꺼짐" :
                !canManage ? "활성 창 확인 또는 전체화면 종료 대기" : pressure.Level < PressureLevel.High ? "메모리 여유 · 정리하지 않음" :
                !needsMemoryRelief ? "앱 복귀 보호 · 가용 RAM 여유로 강제 정리 보류" :
                trimCandidates > 0 ? $"RAM 정리 대상 {trimCandidates}개 · 전역 2분 / 같은 앱 15분 간격" :
                nextIdleSeconds.HasValue ? $"비활성 확인 대기 · 가장 빠른 후보 {Math.Ceiling(nextIdleSeconds.Value)}초 후" : "현재 정리할 비활성 앱 없음 · 사용 중인 작업 보호";
            if (settings.DryRun) memoryStatus = "시뮬레이션 · 실제 RAM 정리 없음 · " + memoryStatus;
            memoryStatus += $" · 누적 정리 {trimCount}회";
            state = new(snap, pressure.Score, pressure.Level, mode, foreground.Name, rows.OrderByDescending(x => x.WorkingSet).ToArray(), events.ToArray(), pending.Count, provider.Topology(), predictions) {
                MemoryStatus = memoryStatus, TrimCount = trimCount, LastWorkingSetReduction = lastWorkingSetReduction
            };
            Audit("tick", now, null, "complete", new { Snapshot = snap, TickElapsedMsBeforeAudit = tickClock.Elapsed.TotalMilliseconds,
                ForegroundPid = foreground.Pid, ForegroundName = foreground.Name, PendingRestores = pending.Count, ProcessCount = processes.Count,
                SystemPagesInputPerSecond = snap.PagesInput, PerProcessHardFaults = (double?)null });
            return state;
        }
    }
    private void Apply(ProcessSample process, Snapshot snapshot, ResourceAction action, bool designated,
        CarpEvidence? evidence, System.Diagnostics.Stopwatch tickClock)
    {
        var now = snapshot.Time;
        if (!provider.Validate(process)) { Audit("apply", now, process, "validation-rejected"); return; }
        var foreground = provider.Foreground(); bool fullscreen = provider.Fullscreen();
        if (settings.DryRun || settings.Paused || foreground.Pid <= 0 || foreground.Name.Length == 0 || fullscreen && !designated || Policy.Protect(process, foreground, settings, Policy.Mode(settings, foreground.Name, snapshot, fullscreen), designated) is not null) {
            Audit("apply", now, process, "final-safety-rejected"); return;
        }
        var old = provider.ReadValues(process.Pid);
        if (old is null || old.CpuPriority is not (0x40 or 0x4000 or 0x20) || provider.Identity(process.Pid) != process.Identity) return;
        uint[] sets = old.CpuSets;
        if (action.HasFlag(ResourceAction.CpuSets)) {
            var topology = provider.Topology(); var efficiency = topology.Where(x => !x.Parked && !x.Allocated).ToArray();
            if (efficiency.Select(x => x.Efficiency).Distinct().Count() > 1 && topology.Select(x => x.Group).Distinct().Count() == 1)
                sets = efficiency.Where(x => x.Efficiency == efficiency.Min(x => x.Efficiency)).Select(x => x.Id).ToArray();
        }
        var value = old with { MemoryPriority = action.HasFlag(ResourceAction.LowerMemory) ? Math.Min(old.MemoryPriority, 3) : old.MemoryPriority,
            CpuPriority = action.HasFlag(ResourceAction.LowerCpu) && old.CpuPriority != 0x40 ? 0x4000U : old.CpuPriority,
            PowerControl = action.HasFlag(ResourceAction.EcoQos) ? old.PowerControl | 1U : old.PowerControl,
            PowerState = action.HasFlag(ResourceAction.EcoQos) ? old.PowerState | 1U : old.PowerState, CpuSets = sets };
        if (!SafeValues(old) || !Audit("apply-intent", now, process, "journal-before-write", new { Original = old, Requested = value, Action = action })) return;
        originals[process.Pid] = new(process, old);
        // 일부 성공이나 강제 종료에 대비해 API 호출 전에 복원할 값을 보존한다.
        if (persist) SaveJournal();
        // A sensor/model/log/journal stall must not turn old observations into
        // an action. Use elapsed monotonic time so replay clocks remain honest.
        bool finalValidated = provider.Validate(process);
        bool evidenceExpired = settings.PolicyName == "carp" && (evidence is null ||
            (snapshot.Time - evidence.ObservedAt).TotalSeconds + tickClock.Elapsed.TotalSeconds > settings.Carp.MaxEvidenceAgeSeconds);
        if (!finalValidated || evidenceExpired) {
            originals.Remove(process.Pid);
            Audit("apply", now, process, evidenceExpired ? "evidence-expired-before-write" : "final-validation-rejected"); return;
        }
        bool ok = provider.WriteValues(process.Pid, value, process.Identity);
        appliedActions[(process.Pid, process.Identity)] = action;
        resourcePolicy.OnActionChanged(process.Pid, process.Identity, action, PolicyNow);
        if (!ok) { pending.Add(process.Pid); failures[process.Pid] = now; }
        Audit("apply", now, process, ok ? "applied" : "partial-failure-restore-pending", new { Requested = value, Action = action });
        Log($"{process.Name} · 우선순위/EcoQoS/CPU Sets {(ok ? "적용" : "일부 실패 · 원복 재시도")}");
    }
    private void Restore(int pid)
    {
        if (!originals.TryGetValue(pid, out var original)) return;
        long? identity = provider.Identity(pid);
        if (identity is null) {
            if (provider.Exited(pid)) { originals.Remove(pid); pending.Remove(pid); appliedActions.Remove((pid, original.Process.Identity)); }
            else pending.Add(pid);
            Audit("restore", DateTimeOffset.UtcNow, original.Process, pending.Contains(pid) ? "identity-unavailable-pending" : "process-exited"); return;
        }
        if (identity != original.Process.Identity) { originals.Remove(pid); pending.Remove(pid); appliedActions.Remove((pid, original.Process.Identity)); Audit("restore", DateTimeOffset.UtcNow, original.Process, "pid-reused-no-write"); return; }
        if (!provider.Validate(original.Process, restoring: true) || !SafeValues(original.Values)) { pending.Add(pid); Audit("restore", DateTimeOffset.UtcNow, original.Process, "validation-pending"); return; }
        bool restored = provider.WriteValues(pid, original.Values, identity.Value);
        if (restored) {
            originals.Remove(pid); pending.Remove(pid); appliedActions.Remove((pid, original.Process.Identity));
            resourcePolicy.OnActionChanged(pid, original.Process.Identity, ResourceAction.None, PolicyNow);
            Log($"{original.Process.Name} · 원래 자원 설정 복원");
        }
        else pending.Add(pid);
        Audit("restore", DateTimeOffset.UtcNow, original.Process, restored ? "restored" : "write-failed-pending", new { Original = original.Values });
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
        if (persist && !dry) try { File.AppendAllText(Path.Combine(root, "events.jsonl"), JsonSerializer.Serialize(new LogEntry(DateTimeOffset.UtcNow, message, dry)) + "\n"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Stop()
    {
        if (remote) { try { EnginePipe.Request<bool>(new("stop")); } catch { } return; }
        Task? task;
        lock (gate) { cancellation?.Cancel(); task = worker; }
        if (task is not null && Task.CurrentId != task.Id) try { task.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        lock (gate) { jobs.Stop(); RestoreAll(); }
    }
    public void Dispose() { if (disposed) return; Stop(); jobs.Dispose(); provider.Dispose(); cancellation?.Dispose(); disposed = true; }
}
