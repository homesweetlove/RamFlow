using RamFlow.Core;

static class StarvationTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
    static readonly CandidateEstimate Cpu = new(ResourceAction.LowerCpu, .5, .05, .01, .01, .95);
    static PolicyInput Input(int second, ResourceAction applied = ResourceAction.LowerCpu) {
        using var provider = new MockResourceProvider();
        var input = new PolicyInput(provider.Current with { Time = Epoch.AddSeconds(second), CpuPercent = 95 },
            provider.Items[0], new Settings { PolicyName = "carp" }, provider.Focus.Pid, provider.Focus.Name,
            false, "background", PressureLevel.Normal, true, 0, true, false, applied);
        return input with { Evidence = new(input.Process.Pid, input.Process.Identity, input.System.Time,
            "synthetic-starvation-fixture", ContentionKind.Cpu, 5, [Cpu]) };
    }

    public static void Run(Action<bool, string> check) {
        var policy = new CarpPolicy(new() { StableSamples = 1 });
        var initial = Input(0);
        policy.OnActionChanged(initial.Process.Pid, initial.Process.Identity, ResourceAction.LowerCpu, Epoch);
        policy.Decide(initial);
        var result = policy.Decide(Input(120));
        check(result.Action == ResourceAction.None, "Unknown progress retained restrictions beyond the 120 second hard limit");
        check(result.Reason.Contains("starvation") && result.Reason.Contains("restricted-seconds=120"), "Hard restore omitted duration/reason audit");

        var defaults = new StarvationOptions();
        check(defaults.StallSeconds >= 10 && defaults.MaxRestrictionSeconds == 120 && defaults.RecoverySeconds >= 30, "Unsafe starvation defaults");
        foreach (var invalid in new[] { defaults with { StallSeconds = 9 }, defaults with { StallSeconds = 601 },
            defaults with { MaxRestrictionSeconds = 0 }, defaults with { MaxRestrictionSeconds = 601 },
            defaults with { RecoverySeconds = 29 }, defaults with { RecoverySeconds = 3601 },
            defaults with { StallSeconds = 100, MaxRestrictionSeconds = 120 } }) {
            bool rejected = false;
            try { _ = new CarpPolicy(new() { Starvation = invalid }); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Invalid starvation options accepted");
        }
        bool badProfile = false;
        try { _ = new CarpPolicy(new() { Ablation = (CarpAblation)999 }); } catch (ArgumentException) { badProfile = true; }
        check(badProfile, "Unknown ablation profile accepted");
        bool nullGuard = false;
        try { _ = new CarpPolicy(new() { Starvation = null! }); } catch (ArgumentException) { nullGuard = true; }
        check(nullGuard, "Null guard options disabled starvation safety");

        foreach (var profile in Enum.GetValues<CarpAblation>()) ProfileSafety(profile, check);
        Progress(check);
        Ablations(check);
        Callbacks(check);
        Capacity(check);
        var slow = Active(CarpAblation.None);
        for (int t = 0; t <= 30; t += 10) {
            var sample = ProgressInput(t, t);
            result = slow.Decide(sample with { Evidence = sample.Evidence! with { BackgroundReferenceUnitsPerSecond = 100 } });
        }
        check(result.Action == ResourceAction.None, "Continuing but severely degraded measured progress bypassed relief");
    }

    static PolicyInput ProgressInput(int second, double? units, string? epoch = "job-1", ResourceAction applied = ResourceAction.LowerCpu) {
        var input = Input(second, applied);
        return input with { Evidence = input.Evidence! with { BackgroundCompletedUnits = units, BackgroundProgressEpoch = epoch } };
    }
    static CarpPolicy Active(CarpAblation profile = CarpAblation.None, ResourceAction action = ResourceAction.LowerCpu) {
        var policy = new CarpPolicy(new() { StableSamples = 1, Ablation = profile });
        var input = Input(0);
        policy.OnActionChanged(input.Process.Pid, input.Process.Identity, action, Epoch);
        return policy;
    }
    static void Changed(CarpPolicy policy, ResourceAction action, int second) {
        var input = Input(second);
        policy.OnActionChanged(input.Process.Pid, input.Process.Identity, action, input.System.Time);
    }
    static void ProfileSafety(CarpAblation profile, Action<bool, string> check) {
        var policy = Active(profile);
        check(policy.Decide(Input(0)).Action == ResourceAction.LowerCpu, profile + " lost supported initial control");
        check(policy.Decide(Input(120)).Action == ResourceAction.None, profile + " bypassed unknown progress deadline");
        Changed(policy, ResourceAction.None, 120);
        check(policy.Decide(Input(121, ResourceAction.None)).Action == ResourceAction.None, profile + " bypassed recovery");
        check(policy.Decide(Input(149, ResourceAction.None)).Action == ResourceAction.None, profile + " shortened recovery");
        check(policy.Decide(Input(151, ResourceAction.None)).Action == ResourceAction.LowerCpu, profile + " never recovered");
        var stalled = Active(profile);
        stalled.Decide(ProgressInput(0, 0));
        var stallRestore = stalled.Decide(ProgressInput(20, 0));
        check(stallRestore.Action == ResourceAction.None, profile + " bypassed observed stall");
        check(stallRestore.Reason.Contains(";starvation-stall-restore") && stallRestore.Reason.Contains("stalled-seconds=20"), profile + " omitted stall restoration cause");
        var bounded = Active(profile);
        for (int second = 0; second < 120; second += 10) bounded.Decide(ProgressInput(second, second));
        check(bounded.Decide(ProgressInput(120, 120)).Action == ResourceAction.None, profile + " let progress bypass duration limit");
        var healthy = Input(0, ResourceAction.None);
        foreach (var unsafeInput in new[] {
            healthy with { Designated = false }, healthy with { ForegroundPid = healthy.Process.Pid },
            healthy with { Settings = healthy.Settings with { Paused = true } },
            healthy with { Settings = healthy.Settings with { Whitelist = [healthy.Process.Name] } },
            healthy with { Process = healthy.Process with { Name = "lsass.exe" } },
            healthy with { Process = healthy.Process with { ActivityKnown = false } },
            healthy with { Evidence = null }, healthy with { Evidence = healthy.Evidence! with { Identity = 999 } },
            healthy with { Evidence = healthy.Evidence! with { ObservedAt = Epoch.AddSeconds(-11) } },
            healthy with { Evidence = healthy.Evidence! with { ObservedAt = Epoch.AddSeconds(1) } },
            healthy with { Evidence = healthy.Evidence! with { Candidates = [Cpu with { BackgroundLoss = double.NaN }] } },
            healthy with { Evidence = healthy.Evidence! with { Candidates = [Cpu with { Confidence = .1 }] } }
        }) check(new CarpPolicy(new() { StableSamples = 1, Ablation = profile }).Decide(unsafeInput).Action == ResourceAction.None,
            profile + " bypassed protection/freshness/identity/validation");
    }
    static void Progress(Action<bool, string> check) {
        var progressing = Active();
        progressing.Decide(ProgressInput(0, 0));
        check(progressing.Decide(ProgressInput(15, 1)).Action == ResourceAction.LowerCpu, "Real observed progress did not extend stall time");
        check(progressing.Decide(ProgressInput(30, 2)).Action == ResourceAction.LowerCpu, "Increasing progress was treated as a stall");
        check(progressing.Decide(ProgressInput(50, 2)).Action == ResourceAction.None, "Stall after observed progress was missed");
        var hard = Active();
        for (int second = 0; second < 120; second += 10) hard.Decide(ProgressInput(second, second));
        check(hard.Decide(ProgressInput(120, 120)).Action == ResourceAction.None, "Progress bypassed absolute duration limit");
        var reset = Active(); reset.Decide(ProgressInput(0, 100));
        reset.Decide(ProgressInput(15, 0, "job-2"));
        check(reset.Decide(ProgressInput(20, 0, "job-2")).Action == ResourceAction.None, "Epoch reset fabricated progress");
        var counterReset = Active(); counterReset.Decide(ProgressInput(0, 100)); counterReset.Decide(ProgressInput(15, 0));
        check(counterReset.Decide(ProgressInput(20, 0)).Action == ResourceAction.None, "Counter reset fabricated progress");
        var missing = Active(); missing.Decide(ProgressInput(0, 0)); missing.Decide(ProgressInput(15, null));
        check(missing.Decide(ProgressInput(20, null)).Action == ResourceAction.None, "Missing progress erased an established stall deadline");
        var duplicate = Active(); duplicate.Decide(ProgressInput(0, 0));
        duplicate.Decide(ProgressInput(10, 1));
        var duplicateInput = ProgressInput(20, 100);
        duplicate.Decide(duplicateInput with { Evidence = duplicateInput.Evidence! with { ObservedAt = Epoch.AddSeconds(10) } });
        var expiredDuplicate = ProgressInput(30, 1);
        check(duplicate.Decide(expiredDuplicate with { Evidence = expiredDuplicate.Evidence! with { ObservedAt = Epoch.AddSeconds(20) } }).Action == ResourceAction.None,
            "Duplicate timestamp fabricated counter progress");
        var unknown = Active(); unknown.Decide(ProgressInput(0, null));
        check(unknown.Decide(ProgressInput(60, null)).Action == ResourceAction.LowerCpu, "Unknown progress was fabricated into a measured stall");
        check(unknown.Decide(ProgressInput(120, null)).Action == ResourceAction.None, "Unknown progress held indefinitely");
        var epochMissing = Active(); epochMissing.Decide(ProgressInput(0, 0, null));
        check(epochMissing.Decide(ProgressInput(120, 999, null)).Action == ResourceAction.None, "Missing epoch bypassed duration bound");
        var throughputOnly = Active();
        var throughput = Input(0);
        throughputOnly.Decide(throughput with { Evidence = throughput.Evidence! with { MeasuredBackgroundUnitsPerSecond = 100 } });
        throughput = Input(120);
        check(throughputOnly.Decide(throughput with { Evidence = throughput.Evidence! with { MeasuredBackgroundUnitsPerSecond = 100 } }).Action == ResourceAction.None,
            "Throughput was fabricated into cumulative progress");
        var lateProgress = Active(); lateProgress.Decide(ProgressInput(0, 0));
        var delayedProgress = ProgressInput(25, 1);
        lateProgress.Decide(delayedProgress with { Evidence = delayedProgress.Evidence! with { ObservedAt = Epoch.AddSeconds(15) } });
        var later = ProgressInput(35, 1);
        check(lateProgress.Decide(later with { Evidence = later.Evidence! with { ObservedAt = Epoch.AddSeconds(25) } }).Action == ResourceAction.None,
            "Arrival time replaced ObservedAt as the progress clock");
        var churn = Active();
        for (int second = 0; second < 120; second += 10) churn.Decide(ProgressInput(second, 0, "epoch-" + second));
        check(churn.Decide(ProgressInput(120, 0, "new-epoch")).Action == ResourceAction.None, "Epoch churn reset duration limit");
        var reuse = Active(); reuse.Decide(ProgressInput(0, 0));
        var newProcess = ProgressInput(21, 0, applied: ResourceAction.None);
        newProcess = newProcess with { Process = newProcess.Process with { Identity = 99 }, Evidence = newProcess.Evidence! with { Identity = 99 } };
        check(reuse.Decide(newProcess).Action == ResourceAction.LowerCpu, "Reused PID inherited previous creation's stall");
        var delayed = Active(); delayed.Decide(ProgressInput(0, 0));
        var sample = ProgressInput(25, 1);
        check(delayed.Decide(sample with { Evidence = sample.Evidence! with { ObservedAt = Epoch.AddSeconds(5) } }).Action == ResourceAction.None,
            "Stale progress retained control");
        var regressed = Active(); regressed.Decide(ProgressInput(10, 1));
        check(regressed.Decide(ProgressInput(9, 2)).Action == ResourceAction.None, "Clock regression extended restriction");
        var invalid = ProgressInput(0, double.NaN, applied: ResourceAction.None);
        check(new CarpPolicy(new() { StableSamples = 1 }).Decide(invalid).Action == ResourceAction.None, "Nonfinite progress was accepted");
    }
    static void Ablations(Action<bool, string> check) {
        var input = Input(0, ResourceAction.None);
        PolicyDecision Decide(CarpAblation profile, PolicyInput sample) => new CarpPolicy(new() { StableSamples = 1, Ablation = profile }).Decide(sample);
        var costlyBackground = input with { Evidence = input.Evidence! with { Candidates = [Cpu with { BackgroundLoss = .9 }] } };
        check(Decide(CarpAblation.None, costlyBackground).Action == ResourceAction.None &&
            Decide(CarpAblation.WithoutBackgroundCost, costlyBackground).Action == ResourceAction.LowerCpu, "Background cost ablation did not remove only that cost");
        var costlyReaccess = input with { Evidence = input.Evidence! with { Candidates = [Cpu with { ReaccessRisk = .9 }] } };
        check(Decide(CarpAblation.None, costlyReaccess).Action == ResourceAction.None &&
            Decide(CarpAblation.WithoutReaccessCost, costlyReaccess).Action == ResourceAction.LowerCpu, "Reaccess ablation did not remove that cost");
        check(Decide(CarpAblation.WithoutBackgroundCost, costlyReaccess).Action == ResourceAction.None &&
            Decide(CarpAblation.WithoutReaccessCost, costlyBackground).Action == ResourceAction.None, "Cost ablation removed an unrelated term");
        var bundle = Cpu with { Action = ResourceAction.LowerCpu | ResourceAction.EcoQos, ForegroundGain = .9 };
        var staged = input with { Evidence = input.Evidence! with { Candidates = [Cpu, bundle] } };
        check(Decide(CarpAblation.None, staged).Action == ResourceAction.LowerCpu &&
            Decide(CarpAblation.WithoutStaging, staged).Action == bundle.Action, "Staging ablation did not change candidate reachability");
        check(new CarpPolicy(new() { Ablation = CarpAblation.WithoutStaging }).Decide(staged).Action == ResourceAction.None,
            "Staging ablation bypassed sample hysteresis");
        check(new CarpPolicy(new() { Ablation = CarpAblation.WithoutStability }).Decide(input).Action == ResourceAction.LowerCpu,
            "Stability ablation did not remove sample hysteresis");
        var cooldown = new CarpPolicy(new() { Ablation = CarpAblation.WithoutStability });
        cooldown.Decide(input); Changed(cooldown, ResourceAction.LowerCpu, 0); Changed(cooldown, ResourceAction.None, 1);
        check(cooldown.Decide(Input(2, ResourceAction.None)).Action == ResourceAction.None, "Stability ablation disabled restore cooldown");
        var mixed = staged with { MemoryPressure = PressureLevel.Moderate };
        mixed = mixed with { Evidence = mixed.Evidence! with { Contention = ContentionKind.Mixed,
            Candidates = [Cpu, Cpu with { Action = ResourceAction.LowerMemory, ForegroundGain = .9 }] } };
        var memoryless = Decide(CarpAblation.WithoutMemoryAwareness, mixed);
        check(memoryless.Action == ResourceAction.LowerCpu && memoryless.Contention == ContentionKind.Mixed &&
            memoryless.Candidates.Where(x => x.Action.HasFlag(ResourceAction.LowerMemory)).All(x => x.Cost is null), "Memory ablation hid pressure or retained a memory action");
        var ram = mixed with { CpuPressure = false, Evidence = mixed.Evidence! with { Contention = ContentionKind.Ram,
            Candidates = [Cpu with { Action = ResourceAction.LowerMemory }] } };
        check(Decide(CarpAblation.WithoutMemoryAwareness, ram).Action == ResourceAction.None, "Memory ablation acted under RAM-only pressure");
        var frozen = new CarpPolicy(new() { StableSamples = 1, Ablation = CarpAblation.WithoutFeedback });
        check(frozen.Decide(input).Action == ResourceAction.LowerCpu, "Feedback profile did not capture initial estimates");
        var changed = Input(1, ResourceAction.None);
        changed = changed with { Evidence = changed.Evidence! with { Candidates = [Cpu with { ForegroundGain = .01 }], MeasuredBackgroundUnitsPerSecond = 7 } };
        var result = frozen.Decide(changed);
        check(result.Action == ResourceAction.LowerCpu && result.Candidates.Any(x => x.Reason.Contains("frozen")), "NoFeedback silently used changing candidate estimates");
        check(Decide(CarpAblation.None, changed).Action == ResourceAction.None, "Live profile ignored changed costs");
        check(frozen.Decide(changed with { Evidence = null }).Action == ResourceAction.None, "Frozen model bypassed fresh evidence requirement");
        var newIdentity = changed with { Process = changed.Process with { Identity = 77 }, Evidence = changed.Evidence! with { Identity = 77 } };
        check(frozen.Decide(newIdentity).Action == ResourceAction.None, "Frozen estimates crossed PID creation identity");
        check(frozen.Decide(changed with { Evidence = changed.Evidence! with { ModelId = "other-model" } }).Action == ResourceAction.None,
            "Frozen estimates crossed model context");
        var changedContext = changed with { MemoryPressure = PressureLevel.Moderate,
            Evidence = changed.Evidence! with { Contention = ContentionKind.Mixed } };
        check(frozen.Decide(changedContext).Action == ResourceAction.None, "Frozen estimates crossed contention context");
        check(frozen.Decide(changed with { Evidence = changed.Evidence! with { Candidates = [Cpu with { BackgroundLoss = double.NaN }] } }).Action == ResourceAction.None,
            "Frozen estimates concealed invalid fresh evidence");
        var frozenProgress = Active(CarpAblation.WithoutFeedback); frozenProgress.Decide(ProgressInput(0, 0));
        check(frozenProgress.Decide(ProgressInput(15, 1)).Action == ResourceAction.LowerCpu &&
            frozenProgress.Decide(ProgressInput(35, 1)).Action == ResourceAction.None, "NoFeedback froze the live starvation guard");
    }
    static void Callbacks(Action<bool, string> check) {
        const ResourceAction all = ResourceAction.LowerCpu | ResourceAction.EcoQos | ResourceAction.LowerMemory;
        PolicyInput Mixed(int second, ResourceAction applied, double units = 0) {
            var input = ProgressInput(second, units, applied: applied) with { MemoryPressure = PressureLevel.Moderate };
            return input with { Evidence = input.Evidence! with { Contention = ContentionKind.Mixed,
                Candidates = [Cpu, Cpu with { Action = ResourceAction.LowerCpu | ResourceAction.LowerMemory, ForegroundGain = .7 },
                    Cpu with { Action = all, ForegroundGain = .9 }] } };
        }
        foreach (var profile in Enum.GetValues<CarpAblation>()) {
            var policy = Active(profile, all); policy.Decide(Mixed(0, all));
            var reduced = policy.Decide(Mixed(20, all));
            check((reduced.Action & ~all) == 0 && !reduced.Action.HasFlag(ResourceAction.EcoQos), profile + " guard failed first stage release");
            if (reduced.Action != ResourceAction.None) {
                Changed(policy, ResourceAction.None, 20); Changed(policy, reduced.Action, 20);
                var next = policy.Decide(Mixed(40, reduced.Action));
                check((next.Action & ~reduced.Action) == 0 && next.Action != reduced.Action, profile + " guard reintroduced controls or reset stall on reapply");
            }
        }
        var continuous = Active(action: all); continuous.Decide(Mixed(0, all));
        var first = continuous.Decide(Mixed(20, all)); Changed(continuous, ResourceAction.None, 20); Changed(continuous, first.Action, 20);
        check(continuous.Decide(Mixed(25, first.Action, 1)).Action == first.Action, "Progress reintroduced a released control");
        check(continuous.Decide(Mixed(120, first.Action, 2)).Action == ResourceAction.None, "Intermediate restore/reapply reset absolute restriction deadline");
        var failed = Active(); failed.Decide(ProgressInput(0, 0)); failed.Decide(ProgressInput(20, 0));
        var retry = failed.Decide(ProgressInput(21, 1));
        check(retry.Action == ResourceAction.None, "Failed restoration was forgotten when progress resumed");
        check(retry.Reason.Contains(";starvation-stall-restore"), "Failed restoration lost its original stall cause");
        Changed(failed, ResourceAction.None, 25);
        check(failed.Decide(Input(54, ResourceAction.None)).Action == ResourceAction.None &&
            failed.Decide(Input(55, ResourceAction.None)).Action == ResourceAction.LowerCpu, "Recovery did not begin at confirmed restoration");
        var observational = new CarpPolicy(new() { StableSamples = 1 }); observational.Decide(Input(0)); observational.Decide(Input(120));
        observational.Decide(Input(121, ResourceAction.None));
        check(observational.Decide(Input(130, ResourceAction.None)).Action == ResourceAction.None, "Observed restore fallback lost recovery");
        var longCooldown = new CarpPolicy(new() { StableSamples = 1, CooldownSeconds = 60 });
        Changed(longCooldown, ResourceAction.LowerCpu, 0); longCooldown.Decide(Input(0)); Changed(longCooldown, ResourceAction.None, 1);
        check(longCooldown.Decide(Input(32, ResourceAction.None)).Action == ResourceAction.None &&
            longCooldown.Decide(Input(61, ResourceAction.None)).Action == ResourceAction.LowerCpu, "Recovery replaced a longer configured cooldown");
        var proposals = new CarpPolicy(new() { StableSamples = 1 });
        proposals.Decide(Input(0, ResourceAction.None));
        check(proposals.Decide(Input(200, ResourceAction.None)).Action == ResourceAction.LowerCpu,
            "Unapplied dry-run recommendations started a restriction clock");
        var reapplied = Active(); reapplied.Decide(Input(0)); reapplied.Decide(Input(120));
        Changed(reapplied, ResourceAction.None, 121); Changed(reapplied, ResourceAction.LowerCpu, 122);
        check(reapplied.Decide(Input(123)).Action == ResourceAction.None, "Unplanned reapply bypassed recovery");
    }
    static void Capacity(Action<bool, string> check) {
        var policy = Active(); policy.Decide(Input(0));
        PolicyDecision? overflow = null;
        for (int i = 0; i < 4100; i++) {
            var input = Input(1, ResourceAction.None);
            int pid = 1000000 + i;
            input = input with { Process = input.Process with { Pid = pid }, Evidence = input.Evidence! with { Pid = pid } };
            overflow = policy.Decide(input);
        }
        check(overflow!.Action == ResourceAction.None && overflow.Reason.Contains("capacity"), "Bounded state overflow did not fail closed");
        check(policy.Decide(Input(3601)).Action == ResourceAction.None, "State pruning forgot an active restriction deadline");
    }
}
