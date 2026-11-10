using System.Diagnostics;
using System.Text.Json;
using RamFlow.Core;

namespace ResearchLab;

sealed record TrialResult(string Status, string Directory, string Regime, bool RegimeAttained);
static class Trial
{
    public static async Task<TrialResult> Run(string[] args) {
        string directory = Path.GetFullPath(Options.Get(args, "--output", "")); Options.NewDirectory(directory);
        string condition = Options.Get(args, "--condition", "A"), intended = Options.Get(args, "--regime", "healthy");
        string split = Options.Get(args, "--split", "pilot"), block = Options.Get(args, "--block", Guid.NewGuid().ToString("N"));
        int seconds = Options.Int(args, "--seconds", 5, 2, 300), seed = Options.Seed(args);
        int foregroundMiB = Options.Int(args, "--foreground-mib", 8, 4, 64), backgroundMiB = Options.Int(args, "--background-mib", 32, 4, 256);
        bool highCpu = args.Contains("--allow-high-cpu");
        int maxThreads = highCpu ? Math.Min(32, Environment.ProcessorCount) : Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
        int threads = Options.Int(args, "--background-threads", Math.Min(2, Math.Max(1, Environment.ProcessorCount / 2)), 1, maxThreads);
        int targetUnits = Options.Int(args, "--background-units", 0, 0, 10000000);
        int gapMs = Options.Int(args, "--request-gap-ms", 5, 0, 1000);
        int candidate = Options.Int(args, "--candidate", 0, 0, 7);
        if (condition is not ("A" or "B" or "C" or "D" or "E" or "F" or "calibration") || intended is not ("cpu" or "ram" or "mixed" or "healthy") ||
            split is not ("fit" or "validation" or "test" or "pilot") || candidate is not (0 or 1 or 3 or 4 or 5 or 7)) throw new ArgumentException("Invalid condition, split, candidate or regime.");
        string modelPath = Options.Get(args, "--model", ""), variant = Options.Get(args, "--variant", "None");
        string mode = intended == "healthy" ? "cpu" : intended;
        string workloadId = $"synthetic-sha-pagewalk-v1:{mode}:fg{foregroundMiB}:bg{backgroundMiB}:threads{threads}:units{targetUnits}:gap{gapMs}:observer-independent-v1";
        Options.WriteNew(Path.Combine(directory, "started.json"), new { condition, intended, split, block, seed, seconds, foregroundMiB, backgroundMiB, threads, workloadId,
            HighCpuExplicit = highCpu,
            SourceCommit = Options.Get(args, "--commit", "unknown"), ControllerSha256 = FileResearchAuditSink.FileHash(typeof(Trial).Assembly.Location),
            CoreSha256 = FileResearchAuditSink.FileHash(typeof(Engine).Assembly.Location), ModelSha256 = modelPath.Length == 0 ? null : FileResearchAuditSink.FileHash(modelPath),
            Sources = Options.CaptureSources(directory), RawSessionSeed = Options.Get(args, "--seed", "1"),
            ForegroundSemantics = "Isolated synthetic foreground-role process, not OS focus, physical input, Chrome or VS Code latency.",
            IntendedPressureIsNotAMeasurement = true });
        string binary = Path.Combine(directory, "fixture-bin"); Directory.CreateDirectory(binary);
        foreach (string file in System.IO.Directory.GetFiles(AppContext.BaseDirectory).Where(x => x.EndsWith(".dll") || x.EndsWith(".json"))) File.Copy(file, Path.Combine(binary, Path.GetFileName(file)));
        string appHost = Path.Combine(AppContext.BaseDirectory, "ResearchLab.exe");
        string foregroundExe = Path.Combine(binary, "ForegroundFixture.exe"), backgroundExe = Path.Combine(binary, "BackgroundFixture.exe");
        File.Copy(appHost, foregroundExe); File.Copy(appHost, backgroundExe);
        var latencies = new List<double>(); var wall = new List<double>(); var snapshots = new List<Snapshot>(); var kinds = new List<ContentionKind>();
        var failures = new List<string>(); bool restored = false, correct = true; double elapsed = 0, units = 0, reaccess = 0, controlCost = 0;
        string hardware = "unavailable", actual = "unknown"; bool attained = false; string? modelId = null; int applies = 0;
        double? completion = null; TimedAudit? audit = null; Engine? engine = null; string modelDiagnostic = "not-read"; int outOfScope = 0;
        ResourceValues? original = null;
        FixtureChild? ownedForeground = null, ownedBackground = null; OwnedProvider? ownedProvider = null;
        IndependentObserver? independent = null; JsonElement[] independentSamples = [];
        using var self = Process.GetCurrentProcess(); using var observer = new ResourceObserver(Path.Combine(directory, "overhead.jsonl"));
        try {
            if (Process.GetCurrentProcess().SessionId <= 0) throw new InvalidOperationException("No interactive user session; session-zero safety protection remains enabled.");
            if (new[] { "RamFlow", "RamFlow.Service", "RamFlow.Tray" }.Any(name => {
                var instances = Process.GetProcessesByName(name); try { return instances.Length > 0; } finally { foreach (var instance in instances) instance.Dispose(); } }))
                throw new InvalidOperationException("An existing RAM Flow instance would confound the isolated comparison. It was left untouched.");
            var foreground = ownedForeground = new FixtureChild(foregroundExe, "foreground", mode, foregroundMiB, 1, seed, 0, Path.Combine(directory, "foreground-raw.jsonl"));
            var background = ownedBackground = new FixtureChild(backgroundExe, "background", mode, backgroundMiB, threads, seed, targetUnits, Path.Combine(directory, "background-raw.jsonl"));
            await Task.WhenAll(foreground.Ready(), background.Ready());
            var provider = ownedProvider = new OwnedProvider(foreground, background);
            var initial = provider.Sample(); hardware = Options.Hardware(initial);
            if (initial.Available < 2UL * 1073741824 || initial.CommitLimit == 0 || (double)initial.Commit / initial.CommitLimit > .85)
                throw new InvalidOperationException("Insufficient safe headroom for a disposable pilot; no pressure was forced.");
            independent = await IndependentObserver.StartAsync(appHost, Path.Combine(directory, "independent"),
                new Dictionary<string, (int, long)> { ["engine-host"] = (self.Id, self.StartTime.ToFileTimeUtc()),
                    ["foreground-fixture"] = (foreground.Process.Id, foreground.Identity), ["background-fixture"] = (background.Process.Id, background.Identity) });
            provider.Processes(); observer.Sample(self, "idle", "engine-host"); await Task.Delay(200);
            observer.Sample(self, "idle", "engine-host");
            original = provider.ReadValues(background.Process.Id) ?? throw new IOException("Cannot read original child settings.");
            string? expected = null;
            for (int i = 0; i < 5; i++) { var warmup = await foreground.Request("work"); expected ??= warmup.Checksum; if (warmup.Checksum != expected) throw new IOException("Warmup correctness mismatch."); }
            bool usesModel = condition is "B" or "E" or "F";
            CalibratedObservationSource? modelSource = modelPath.Length == 0 || !usesModel ? null : new(CostModelTrainer.Load(modelPath), hardware, workloadId,
                (process, snapshot) => process.Pid == background.Process.Id && process.Identity == background.Identity && background.Progress is { ObservedUtc: {} time } progress && time <= snapshot.Time ?
                    new(progress.Units, block, time) : null);
            if (modelPath.Length > 0 && usesModel) modelId = CostModelTrainer.Load(modelPath).ModelId;
            audit = condition == "A" ? null : new TimedAudit(Path.Combine(directory, "audit"), Options.Get(args, "--commit", "unknown"));
            var config = new Settings { DryRun = condition == "B", PolicyName = condition is "C" or "calibration" ? "fixed" : condition == "D" ? "v1" : "carp",
                WorkingSetTrim = false, CpuSets = false, BackgroundApps = [backgroundExe], Carp = LabProfiles.Options(variant) };
            if (condition != "A") {
                engine = new(provider, Path.Combine(directory, "engine-data"), persist: true, allowRemote: false, observations: modelSource, audit: audit,
                    researchPolicy: condition == "calibration" ? new CandidatePolicy((ResourceAction)candidate) : null);
                engine.ApplySettings(config);
            }
            independent.SetPhase("observe"); observer.Sample(self, "observe", "engine-host");
            provider.Sample(); provider.Processes(); await Task.Delay(200); observer.Sample(self, "observe", "engine-host");
            independent.SetPhase("evaluate"); string observerPhase = "evaluate";
            long firstUnits = (await background.Request("progress")).Units;
            var measurement = Stopwatch.StartNew(); double nextTick = 0, nextSample = 0; var pressure = new Pressure(); bool cpuPressure = false;
            TimeSpan cpuStart = self.TotalProcessorTime;
            while (measurement.Elapsed.TotalSeconds < seconds || latencies.Count < 30) {
                if (measurement.Elapsed.TotalSeconds > seconds + 15) throw new TimeoutException("Minimum foreground observations not completed within bounded trial.");
                if (measurement.Elapsed.TotalSeconds >= nextTick) {
                    if (engine is not null) { var state = engine.Tick(); snapshots.Add(state.Snapshot); kinds.Add(Kind(state.Snapshot, state.Level, ref cpuPressure)); }
                    else { var snapshot = provider.Sample(); snapshots.Add(snapshot); pressure.Update(snapshot, snapshot.RamTotal >= 32UL * 1073741824 ? -5 : snapshot.RamTotal <= 8UL * 1073741824 ? 2 : 0); kinds.Add(Kind(snapshot, pressure.Level, ref cpuPressure)); }
                    nextTick = measurement.Elapsed.TotalSeconds + 1;
                    var last = snapshots[^1];
                    if (last.Available < 512 * 1048576UL || (double)last.Commit / last.CommitLimit > .9) throw new InvalidOperationException("Safety headroom release: aborting owned fixtures.");
                }
                var request = Stopwatch.StartNew(); var reply = await foreground.Request("work"); wall.Add(request.Elapsed.TotalMilliseconds);
                if (reply.LatencyMs is not double latency || !double.IsFinite(latency) || latency <= 0) throw new IOException("Missing measured foreground latency.");
                latencies.Add(latency); correct &= reply.Checksum == expected;
                if (gapMs > 0) await Task.Delay(gapMs);
                if (measurement.Elapsed.TotalSeconds >= nextSample) {
                    string phase = condition == "A" ? "idle" : condition == "B" ? "evaluate" : audit?.Applies > 0 ? "intervene" : "evaluate";
                    if (phase != observerPhase) { independent.SetPhase(phase); observerPhase = phase; }
                    if (audit is not null) audit.Phase = phase;
                    observer.Sample(self, phase, "engine-host", audit?.DecisionMs.LastOrDefault(), audit?.WriteMs.LastOrDefault());
                    observer.Sample(foreground.Process, phase, "foreground-fixture"); observer.Sample(background.Process, phase, "background-fixture");
                    nextSample = measurement.Elapsed.TotalSeconds + .25;
                }
            }
            elapsed = measurement.Elapsed.TotalSeconds; units = Math.Max(0, (await background.Request("progress")).Units - firstUnits);
            controlCost = Math.Clamp((self.TotalProcessorTime - cpuStart).TotalSeconds / elapsed / Environment.ProcessorCount, 0, 1);
            // Probe while restriction is in effect. This is a measured page-walk/
            // scheduling proxy, never a per-process disk-hard-fault measurement.
            reaccess = (await background.Request("probe")).LatencyMs ?? throw new IOException("Missing reaccess proxy.");
            completion = background.Progress?.CompletionSeconds;
            actual = kinds.Count == 0 ? "unknown" : kinds.GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key.ToString().ToLowerInvariant(); attained = actual == intended;
            applies = audit?.Applies ?? 0;
            modelDiagnostic = modelSource?.Diagnostic ?? "no-model-supplied";
            independent.SetPhase("restore"); if (audit is not null) audit.Phase = "restore";
            observer.Sample(self, "restore", "engine-host"); provider.ProbeForeground = true; engine?.Tick(); engine?.Stop();
            await Task.Delay(200); observer.Sample(self, "restore", "engine-host");
            var final = provider.ReadValues(background.Process.Id);
            restored = final is not null && Equal(original, final) && (engine?.GetState().PendingRestores ?? 0) == 0;
            if (!restored) failures.Add("Original child resource settings did not restore.");
            if (provider.OutOfScopeRequests != 0) failures.Add("Out-of-scope setter request was rejected.");
            if (!correct) failures.Add("Foreground checksum mismatch.");
            if ((condition == "C" || condition == "calibration" && candidate != 0) && applies == 0)
                failures.Add("The declared fixed/calibration intervention was not applied; do not train on a nominal treatment.");
            outOfScope = provider.OutOfScopeRequests;
            bool stable = kinds.Count > 0 && kinds.Count(x => x.ToString().Equals(actual, StringComparison.OrdinalIgnoreCase)) >= .8 * kinds.Count;
            if (!stable) failures.Add("Observed pressure regime changed within trial; retained as invalid calibration.");
            await independent.StopAsync(); independentSamples = independent.ReadSamples();
            string status = failures.Count == 0 ? "completed" : "invalid";
            Options.WriteNew(Path.Combine(directory, "cost-observation.json"), new CostObservation(block, split, hardware, workloadId,
                Enum.TryParse<ContentionKind>(actual, true, out var regime) ? regime : ContentionKind.Unknown,
                condition == "calibration" ? (ResourceAction)candidate : ResourceAction.None,
                snapshots.Count == 0 ? CostFeatures.From(initial) : CostFeatures.From(snapshots[snapshots.Count / 2]),
                Quantile(latencies, .95), units / elapsed, reaccess, controlCost, status == "completed" && condition == "calibration"));
            SaveOutcome(status); return new(status, directory, actual, attained);
        } catch (Exception error) {
            failures.Add(error.ToString());
            try {
                engine?.Stop();
                var readback = ownedBackground is null ? null : ownedProvider?.ReadValues(ownedBackground.Process.Id);
                restored = original is not null && readback is not null && Equal(original, readback) && (engine?.GetState().PendingRestores ?? 0) == 0;
            } catch (Exception restoreError) { failures.Add(restoreError.ToString()); }
            if (independent is not null) try { await independent.StopAsync(); independentSamples = independent.ReadSamples(); } catch (Exception observerError) { failures.Add(observerError.ToString()); }
            SaveOutcome("failed"); return new("failed", directory, actual, attained);
        } finally {
            try { engine?.Dispose(); } finally {
                ownedProvider?.Dispose();
                if (independent is not null) await independent.DisposeAsync();
                if (ownedBackground is not null) await ownedBackground.DisposeAsync();
                if (ownedForeground is not null) await ownedForeground.DisposeAsync();
                audit?.Dispose();
            }
        }

        void SaveOutcome(string status) {
            Options.WriteNew(Path.Combine(directory, "outcome.json"), new { schema_version = 2, session_block_id = block, hardware_id = hardware,
                workload_id = workloadId, split, condition, regime = actual, intended_regime = intended, regime_attained = attained,
                candidate_action = condition == "calibration" ? (int?)candidate : condition == "C" ? 3 : condition is "A" or "B" ? 0 : null, status, foreground_latency_ms = latencies,
                foreground_wall_ms = wall, background_units = units, measurement_seconds = elapsed, background_completion_seconds = completion,
                background_completion_scope = targetUnits == 0 ? "continuous fixture; completion not applicable" : "right censored if target not completed during observation",
                reaccess_ms = reaccess > 0 ? (double?)reaccess : null, reaccess_scope = "owned background page-walk/SHA scheduling proxy, not disk hard-fault latency",
                correctness_passed = correct, restored, policy_variant = condition == "calibration" ? "candidate-" + candidate : variant, model_id = modelId, model_status = modelPath.Length == 0 ? "no-model-supplied" : "frozen-model",
                actual_apply_count = applies, overhead_samples = independent is null ? observer.Samples : (object)independentSamples.Cast<object>().Concat(audit?.TimingSamples ?? []).ToArray(),
                controller_internal_samples = observer.Samples, overhead_scope = "independent sampler separates observer; engine-host still includes orchestration, tray/service absent, not measured as zero",
                model_diagnostic = modelDiagnostic, out_of_scope_requests = outOfScope,
                system_snapshots = snapshots, failures, completed_utc = DateTimeOffset.UtcNow });
        }
    }
    static ContentionKind Kind(Snapshot s, PressureLevel pressure, ref bool cpu) { cpu = s.CpuPercent >= 80 || cpu && s.CpuPercent >= 60;
        return (cpu, pressure >= PressureLevel.Moderate) switch { (true, true) => ContentionKind.Mixed, (true, false) => ContentionKind.Cpu,
            (false, true) => ContentionKind.Ram, _ => ContentionKind.Healthy }; }
    public static double Quantile(IEnumerable<double> values, double p) { var sorted = values.Order().ToArray(); if (sorted.Length == 0) return double.NaN;
        double index = (sorted.Length - 1) * p; int lower = (int)index; return sorted[lower] + (sorted[Math.Min(lower + 1, sorted.Length - 1)] - sorted[lower]) * (index - lower); }
    static bool Equal(ResourceValues a, ResourceValues b) => a.CpuPriority == b.CpuPriority && a.MemoryPriority == b.MemoryPriority &&
        a.PowerControl == b.PowerControl && a.PowerState == b.PowerState && a.CpuSets.SequenceEqual(b.CpuSets);
}
