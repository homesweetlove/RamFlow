using RamFlow.Core;
using System.Text.Json;

static class ResearchPolicyTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
    static PolicyInput Input() {
        var p = new MockResourceProvider();
        var s = new Settings { PolicyName = "carp", DryRun = false, WorkingSetTrim = true, BackgroundApps = [p.Items[0].Path] };
        return new(p.Current with { Time = Epoch, CpuPercent = 95 }, p.Items[0], s,
            p.Focus.Pid, p.Focus.Name, false, "background", PressureLevel.Normal, true, 0, true, false);
    }
    static CarpEvidence Evidence(PolicyInput input, params CandidateEstimate[] candidates) => new(
        input.Process.Pid, input.Process.Identity, input.System.Time, "pilot-fixture-not-performance", ContentionKind.Cpu, 5, candidates);
    static readonly CandidateEstimate Cpu = new(ResourceAction.LowerCpu, .35, .10, .01, .01, .95);
    static readonly CandidateEstimate Eco = new(ResourceAction.LowerCpu | ResourceAction.EcoQos, .40, .60, .01, .02, .95);
    public static void Run(Action<bool, string> check) {
        var input = Input();
        check(new CarpPolicy().Decide(input).Action == ResourceAction.None, "CARP fabricated missing observations");
        var policy = new CarpPolicy(new() { StableSamples = 1 });
        var known = input with { Evidence = Evidence(input, Cpu, Eco) };
        var result = policy.Decide(known);
        check(result.Action == ResourceAction.LowerCpu, "CARP ignored the cheaper foreground/background tradeoff");
        check(result.Candidates.Count == 3 && result.Candidates.Single(x => x.Action == ResourceAction.LowerCpu).Cost < 0,
            "Cost audit omitted no-action or estimates");
        check(policy.Decide(known with { Evidence = known.Evidence! with { Identity = 99 } }).Action == ResourceAction.None, "CARP accepted reused PID evidence");
        check(policy.Decide(known with { System = known.System with { Time = Epoch.AddSeconds(11) } }).Action == ResourceAction.None, "CARP accepted stale evidence");
        check(policy.Decide(known with { Evidence = known.Evidence! with { ObservedAt = Epoch.AddSeconds(1) } }).Action == ResourceAction.None, "CARP accepted future evidence");
        check(policy.Decide(input with { Evidence = Evidence(input, Cpu with { BackgroundLoss = double.NaN }) }).Action == ResourceAction.None, "CARP accepted NaN costs");
        check(policy.Decide(input with { Evidence = Evidence(input, Cpu with { Confidence = .1 }) }).Action == ResourceAction.None, "CARP accepted low confidence");
        check(policy.Decide(input with { Evidence = Evidence(input, Eco) }).Action == ResourceAction.None, "CARP selected net harm");
        var ram = input with { CpuPressure = false, MemoryPressure = PressureLevel.High };
        ram = ram with { Evidence = Evidence(ram, new CandidateEstimate(ResourceAction.LowerMemory, .3, .05, .02, .01, .9)) with { Contention = ContentionKind.Ram } };
        check(new CarpPolicy(new() { StableSamples = 1 }).Decide(ram).Action == ResourceAction.LowerMemory, "RAM policy could not select supported memory-only control");
        check(new CarpPolicy().Decide(ram with { Evidence = ram.Evidence! with { Candidates = [new(ResourceAction.LowerMemory, .3, .05, .5, .01, .9)] } }).Action == ResourceAction.None,
            "CARP ignored dominant page reaccess risk");
        bool invalidConfig = false; try { Engine.Validate(input.Settings with { Carp = new() { BackgroundWeight = double.NaN } }); } catch (ArgumentException) { invalidConfig = true; }
        check(invalidConfig, "Nonfinite CARP configuration accepted");
        check(policy.Decide(known with { Designated = false }).Action == ResourceAction.None, "CARP changed an unselected app");
        check(policy.Decide(known with { ForegroundPid = input.Process.Pid }).Action == ResourceAction.None, "CARP bypassed foreground protection");
        check(policy.Decide(known with { Process = input.Process with { Name = "lsass.exe" } }).Action == ResourceAction.None, "CARP bypassed security protection");
        check(policy.Decide(known with { Settings = input.Settings with { Whitelist = [input.Process.Name] } }).Action == ResourceAction.None, "CARP bypassed whitelist");
        check(policy.Decide(known with { Process = input.Process with { ActivityKnown = false } }).Action == ResourceAction.None, "CARP bypassed unknown activity protection");
        check(policy.Decide(known with { System = input.System with { CpuPercent = 10 }, CpuPressure = false }).Action == ResourceAction.None, "CARP intervened in healthy state");
        var gradual = new CarpPolicy();
        check(gradual.Decide(known).Action == ResourceAction.None, "CARP ignored entry hysteresis");
        check(gradual.Decide(known).Action == ResourceAction.None, "Duplicate snapshot counted as independent evidence");
        check(gradual.Decide(known with { System = known.System with { Time = Epoch.AddSeconds(1) } }).Action == ResourceAction.None, "Repeated evidence counted as a new observation");
        for (int n = 1; n <= 2; n++) {
            var next = known with { System = known.System with { Time = Epoch.AddSeconds(n * 2) }, Evidence = known.Evidence! with { ObservedAt = Epoch.AddSeconds(n * 2) } };
            result = gradual.Decide(next);
        }
        check(result.Action == ResourceAction.LowerCpu, "CARP never entered after stable observations");
        var active = known with { CurrentAction = ResourceAction.LowerCpu };
        check(gradual.Decide(active with { Evidence = null }).Action == ResourceAction.None, "CARP retained control after evidence loss");
        var stronger = known with { Evidence = Evidence(input, Cpu, Eco with { ForegroundGain = .9, BackgroundLoss = .1 }) };
        check(new CarpPolicy(new() { StableSamples = 1 }).Decide(stronger).Action == ResourceAction.LowerCpu, "CARP skipped minimum intervention stage");
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new FixtureSource())) {
            p.Current = input.System; engine.ApplySettings(input.Settings with { Carp = new() { StableSamples = 1 } }); engine.Tick();
            check(p.Values[987650].CpuPriority == 0x4000 && p.Values[987650].MemoryPriority == 5 && p.Values[987650].PowerState == 0, "CARP application bundled unrelated controls");
            check(p.Trims == 0, "CARP forcibly evicted working sets");
            p.Focus = (987650, p.Items[0].Name); engine.Tick();
            check(p.Values[987650].CpuPriority == 0x20, "CARP foreground restoration failed");
        }
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new FixtureSource())) {
            p.Current = input.System; engine.ApplySettings(input.Settings with { DryRun = true, Carp = new() { StableSamples = 1 } }); engine.Tick();
            check(p.Writes == 0 && p.Trims == 0, "CARP Dry Run modified resources");
        }
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false)) {
            p.Current = input.System; engine.ApplySettings(input.Settings); engine.Tick();
            check(p.Writes == 0 && p.Trims == 0, "Default CARP source invented measurements");
        }
        string root = Path.Combine(Path.GetTempPath(), "RamFlow.Research-" + Guid.NewGuid().ToString("N"));
        using (var sink = new FileResearchAuditSink(root, "test-commit")) {
            using var p = new MockResourceProvider();
            using (var engine = new Engine(p, persist: false, observations: new FixtureSource(), audit: sink)) {
                p.Current = input.System; engine.ApplySettings(input.Settings with { Carp = new() { StableSamples = 1 } }); engine.Tick(); engine.Stop();
            }
        }
        check(File.Exists(Path.Combine(root, "manifest.json")), "Research manifest missing");
        var rows = File.ReadAllLines(Path.Combine(root, "events.jsonl")).Select(x => JsonDocument.Parse(x)).ToArray();
        check(rows.Any(x => x.RootElement.GetProperty("Kind").GetString() == "decision") && rows.Any(x => x.RootElement.GetProperty("Kind").GetString() == "restore"), "Research audit omitted decision or restore");
        foreach (var row in rows) row.Dispose();
        bool rejected = false; try { using var duplicate = new FileResearchAuditSink(root, "test"); } catch (IOException) { rejected = true; }
        check(rejected, "Research log overwrote an existing run");
        string observationPath = Path.Combine(root, "observations.json");
        File.WriteAllText(observationPath, JsonSerializer.Serialize(new[] { known.Evidence }));
        var source = new JsonPolicyObservationSource(observationPath);
        check(source.Read(input.Process, input.System)?.ModelId == known.Evidence!.ModelId, "Explicit observation file was not read");
        check(source.Read(input.Process with { Identity = 999 }, input.System) is null, "Observation adapter crossed PID creation identity");
        File.WriteAllText(observationPath, JsonSerializer.Serialize(new[] { known.Evidence, known.Evidence }));
        bool duplicateRejected = false;
        try { source.Read(input.Process, input.System with { Time = Epoch.AddSeconds(1) }); } catch (ArgumentException) { duplicateRejected = true; }
        check(duplicateRejected, "Observation adapter accepted ambiguous duplicate identities");
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new FixtureSource(), audit: new BrokenSink())) {
            p.Current = input.System; engine.ApplySettings(input.Settings with { Carp = new() { StableSamples = 1 } }); engine.Tick();
            check(p.Writes == 0, "Audit failure allowed a new research intervention");
        }
        var changingSink = new ToggleSink();
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new FixtureSource(), audit: changingSink)) {
            p.Current = input.System; engine.ApplySettings(input.Settings with { Carp = new() { StableSamples = 1 } }); engine.Tick();
            changingSink.Fail = true; p.Focus = (987650, p.Items[0].Name); engine.Tick();
            check(p.Values[987650].CpuPriority == 0x20, "Audit failure prevented safety restoration");
        }
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new FixtureSource())) {
            p.Current = input.System; engine.ApplySettings(input.Settings with { Carp = new() { StableSamples = 1 } }); engine.Tick();
            p.FailWrite = true; p.Focus = (987650, p.Items[0].Name); engine.Tick();
            check(engine.GetState().PendingRestores == 1, "CARP lost failed restore state");
            p.FailWrite = false; engine.Tick();
            check(engine.GetState().PendingRestores == 0 && p.Values[987650].CpuPriority == 0x20, "CARP did not retry restoration");
        }
        var recency = new CarpPolicy();
        recency.Decide(known);
        recency.Decide(known with { System = known.System with { Time = Epoch.AddSeconds(2) } });
        check(recency.Decide(known with { System = known.System with { Time = Epoch.AddSeconds(4) } }).Action == ResourceAction.None, "Cached evidence satisfied hysteresis without new observations");
        check(new CarpPolicy().Decide(known with { Evidence = known.Evidence! with { Candidates = [null!] } }).Action == ResourceAction.None, "Null candidate did not fail closed");
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new FixtureSource(), audit: new UnexpectedFailureSink())) {
            p.Current = input.System; engine.ApplySettings(input.Settings); engine.Tick();
            check(p.Writes == 0, "Unexpected audit failure escaped safety boundary");
        }
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, persist: false, observations: new SlowSource())) {
            p.Current = input.System; engine.ApplySettings(input.Settings with { Carp = new() { StableSamples = 1, MaxEvidenceAgeSeconds = 1 } }); engine.Tick();
            check(p.Writes == 0, "Evidence expired while being read but was still applied");
        }
        var transition = new CarpPolicy();
        PolicyInput At(int second, ResourceAction applied, bool mixed = false) {
            var next = known with { System = known.System with { Time = Epoch.AddSeconds(second) }, CurrentAction = applied,
                MemoryPressure = mixed ? PressureLevel.Moderate : PressureLevel.Normal };
            return next with { Evidence = Evidence(next, Cpu) with { Contention = mixed ? ContentionKind.Mixed : ContentionKind.Cpu } };
        }
        transition.Decide(At(0, ResourceAction.None)); transition.Decide(At(2, ResourceAction.None)); transition.Decide(At(4, ResourceAction.None));
        transition.Decide(At(6, ResourceAction.LowerCpu));
        var mixedInput = At(8, ResourceAction.LowerCpu, true);
        mixedInput = mixedInput with { Evidence = mixedInput.Evidence! with { Candidates = [Cpu, new(ResourceAction.LowerMemory, .7, .1, 0, .01, .95)] } };
        check(transition.Decide(mixedInput).Action == ResourceAction.LowerCpu, "Equal-strength control substitution bypassed entry gates");
        transition.Decide(At(10, ResourceAction.LowerCpu) with { Evidence = null });
        transition.Decide(At(12, ResourceAction.None)); transition.Decide(At(14, ResourceAction.None));
        check(transition.Decide(At(16, ResourceAction.None)).Action == ResourceAction.None, "Evidence loss erased restoration cooldown");
        var delayedObservations = new CarpPolicy(); delayedObservations.Decide(input);
        for (int second = 2; second <= 10; second += 2) {
            var delayed = At(second, ResourceAction.None);
            result = delayedObservations.Decide(delayed with { Evidence = delayed.Evidence! with { ObservedAt = Epoch.AddSeconds(second - 3) } });
        }
        check(result.Action == ResourceAction.LowerCpu, "Valid consistently delayed observations never recovered after evidence loss");
        var betweenTicks = new CarpPolicy(); betweenTicks.Decide(At(0, ResourceAction.None)); betweenTicks.Decide(At(2, ResourceAction.None)); betweenTicks.Decide(At(4, ResourceAction.None));
        betweenTicks.OnActionChanged(input.Process.Pid, input.Process.Identity, ResourceAction.LowerCpu, Epoch.AddSeconds(4));
        betweenTicks.OnActionChanged(input.Process.Pid, input.Process.Identity, ResourceAction.None, Epoch.AddSeconds(5));
        check(betweenTicks.Decide(At(6, ResourceAction.None)).Action == ResourceAction.None, "Between-tick foreground restoration bypassed hysteresis");
        betweenTicks.Decide(At(8, ResourceAction.None));
        check(betweenTicks.Decide(At(10, ResourceAction.None)).Action == ResourceAction.None, "Between-tick restoration bypassed cooldown");
        string blockedLogRoot = Path.Combine(root, "blocked-log"); Directory.CreateDirectory(Path.Combine(blockedLogRoot, "events.jsonl"));
        using (var p = new MockResourceProvider()) using (var engine = new Engine(p, blockedLogRoot, observations: new FixtureSource(), audit: new UnexpectedFailureSink())) {
            p.Current = input.System; engine.ApplySettings(input.Settings); engine.Tick();
            check(p.Writes == 0, "Audit fallback log failure escaped safety boundary");
        }
        // Retain logs in the test temp directory; no recursive cleanup of an inferred path.
        Console.WriteLine("Research policy, evidence, audit and safety regressions passed.");
    }
    sealed class FixtureSource : IPolicyObservationSource {
        public CarpEvidence? Read(ProcessSample process, Snapshot snapshot) => new(process.Pid, process.Identity, snapshot.Time,
            "fixture-not-calibrated", ContentionKind.Cpu, 5, [Cpu]);
    }
    sealed class BrokenSink : IResearchAuditSink { public void Write(ResearchAuditEvent entry) => throw new IOException("fixture-full-disk"); }
    sealed class ToggleSink : IResearchAuditSink { public bool Fail; public void Write(ResearchAuditEvent entry) { if (Fail) throw new IOException("fixture-full-disk"); } }
    sealed class UnexpectedFailureSink : IResearchAuditSink { public void Write(ResearchAuditEvent entry) => throw new InvalidOperationException("fixture-sink-failed"); }
    sealed class SlowSource : IPolicyObservationSource { public CarpEvidence? Read(ProcessSample p, Snapshot s) { Thread.Sleep(1100); return new FixtureSource().Read(p, s); } }
}
