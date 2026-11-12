namespace RamFlow.Core;

public sealed record StarvationOptions
{
    public int StallSeconds { get; init; } = 20;
    public int MaxRestrictionSeconds { get; init; } = 120;
    public int RecoverySeconds { get; init; } = 30;
    public double MinProgressFraction { get; init; } = .1;

    public static void Validate(StarvationOptions value) {
        if (value is null || value.StallSeconds is < 10 or > 300 ||
            value.MaxRestrictionSeconds is < 30 or > 600 || value.MaxRestrictionSeconds < 2 * value.StallSeconds ||
            value.RecoverySeconds is < 30 or > 3600 || !double.IsFinite(value.MinProgressFraction) || value.MinProgressFraction is <= 0 or > 1)
            throw new ArgumentException("Invalid starvation safety configuration.");
    }
}

// A final, profile-independent veto. It never performs process actions or adds
// controls, and clocks start on applied/observed transitions, not recommendations.
internal sealed class StarvationGuard(StarvationOptions options)
{
    sealed class State(DateTimeOffset time)
    {
        public DateTimeOffset Touched = time;
        public ResourceAction Applied, Expected;
        public DateTimeOffset? Started, DeferredRestore, RecoveryUntil;
        public DateTimeOffset? SlowSince;
        public DateTimeOffset ProgressSeen = DateTimeOffset.MinValue, LastProgress;
        public double? Units;
        public string? Epoch;
        public bool HasProgress;
        public string? RestoreReason;
        public ResourceAction? Ceiling;
        public int Released;
    }
    const int Capacity = 4096;
    readonly Dictionary<(int, long), State> states = new();

    State? Get(int pid, long identity, DateTimeOffset time) {
        if (pid <= 0 || identity <= 0) return null;
        foreach (var key in states.Where(x => x.Value.Applied == ResourceAction.None && x.Value.Started is null &&
            (x.Value.RecoveryUntil is null || time >= x.Value.RecoveryUntil) &&
            time - x.Value.Touched > TimeSpan.FromHours(1)).Select(x => x.Key).ToArray()) states.Remove(key);
        if (states.TryGetValue((pid, identity), out var state)) return state;
        if (states.Count >= Capacity) return null; // Never evict an active deadline or recovery.
        states[(pid, identity)] = state = new(time);
        return state;
    }

    public void OnActionChanged(int pid, long identity, ResourceAction action, DateTimeOffset time) {
        var state = Get(pid, identity, time);
        if (state is not null) Change(state, action, time);
    }

    void Restored(State state, DateTimeOffset time) {
        state.Applied = ResourceAction.None;
        state.Started = state.DeferredRestore = null;
        state.RecoveryUntil = time.AddSeconds(options.RecoverySeconds);
        state.Expected = ResourceAction.None;
        state.Ceiling = null;
        state.RestoreReason = null;
        state.HasProgress = false;
        state.SlowSince = null;
        state.ProgressSeen = DateTimeOffset.MinValue;
        state.Units = null;
        state.Epoch = null;
        state.Released = 0;
    }

    void Change(State state, ResourceAction action, DateTimeOffset time) {
        if (time < state.Touched) {
            if (state.Started is not null) state.RestoreReason ??= "starvation-clock-regressed-restore";
            time = state.Touched;
        }
        state.Touched = time;
        if (action == ResourceAction.None) {
            if (state.Applied != ResourceAction.None) {
                // Engine restores originals before applying a different action.
                // Retain the deadline until reapply succeeds or a later tick
                // confirms that the process actually remains unrestricted.
                if (state.Expected != ResourceAction.None && state.Expected != state.Applied) {
                    state.DeferredRestore = time;
                    state.Applied = ResourceAction.None;
                } else Restored(state, time);
            }
            return;
        }
        if (state.DeferredRestore is not null && action == state.Expected) state.DeferredRestore = null;
        else if (state.Started is null) {
            state.Started = state.LastProgress = time;
            state.ProgressSeen = DateTimeOffset.MinValue;
            state.Units = null;
            state.Epoch = null;
            state.HasProgress = false;
            state.SlowSince = null;
            state.Ceiling = null;
            state.Released = 0;
            state.RestoreReason = state.RecoveryUntil is not null && time < state.RecoveryUntil ? "starvation-recovery-violation-restore" : null;
        }
        state.Applied = action;
        state.Expected = ResourceAction.None;
    }

    public PolicyDecision Constrain(PolicyInput input, PolicyDecision decision, CarpEvidence? validEvidence) {
        var now = input.System.Time;
        var state = Get(input.Process.Pid, input.Process.Identity, now);
        if (state is null) return decision with { Action = ResourceAction.None, Reason = decision.Reason + ";starvation-state-capacity-or-identity" };
        bool regressed = now < state.Touched;
        if (state.DeferredRestore is DateTimeOffset restored && input.CurrentAction == ResourceAction.None) Restored(state, restored);
        if (state.Applied != input.CurrentAction) {
            Change(state, input.CurrentAction, now);
            if (state.DeferredRestore is DateTimeOffset observedRestore) Restored(state, observedRestore);
        }
        if (now > state.Touched) state.Touched = now;
        double restricted = state.Started is DateTimeOffset start ? Math.Max(0, (state.Touched - start).TotalSeconds) : 0;
        string status = "starvation-unrestricted";
        ResourceAction action = decision.Action;
        if (regressed) {
            if (state.Started is not null) state.RestoreReason ??= "starvation-clock-regressed-restore";
            action = ResourceAction.None;
            status = "starvation-clock-regressed-restore";
        } else if (state.RecoveryUntil is DateTimeOffset until && now < until) {
            action = ResourceAction.None;
            status = "starvation-recovery-window";
        } else if (state.Started is not null) {
            if (validEvidence is { BackgroundCompletedUnits: double units, BackgroundProgressEpoch: string epoch } &&
                !string.IsNullOrWhiteSpace(epoch) && epoch.Length <= 256 && double.IsFinite(units) && units >= 0 &&
                validEvidence.ObservedAt > state.ProgressSeen) {
                if (state.Units is double old && state.Epoch == epoch && units > old)
                    state.LastProgress = validEvidence.ObservedAt > state.LastProgress ? validEvidence.ObservedAt : state.LastProgress;
                if (state.Units is double previousUnits && state.Epoch == epoch && validEvidence.ObservedAt > state.ProgressSeen && units >= previousUnits &&
                    validEvidence.BackgroundReferenceUnitsPerSecond is double reference && double.IsFinite(reference) && reference > 0) {
                    double rate = (units - previousUnits) / (validEvidence.ObservedAt - state.ProgressSeen).TotalSeconds;
                    if (rate < options.MinProgressFraction * reference) state.SlowSince ??= validEvidence.ObservedAt;
                    else state.SlowSince = null;
                }
                state.ProgressSeen = validEvidence.ObservedAt;
                state.Units = units;
                state.Epoch = epoch;
                state.HasProgress = true;
            }
            if (restricted >= options.MaxRestrictionSeconds) {
                state.RestoreReason ??= "starvation-max-restriction-restore";
            } else if (state.HasProgress) {
                int releases = (int)Math.Min(4, Math.Max(0, (now - state.LastProgress).TotalSeconds) / options.StallSeconds);
                if (state.SlowSince is {} slow) releases = Math.Max(releases, (int)Math.Min(4, Math.Max(0, (now - slow).TotalSeconds) / options.StallSeconds));
                if (releases > state.Released) {
                    var ceiling = (state.Ceiling ?? input.CurrentAction) & input.CurrentAction;
                    for (int n = state.Released; n < releases; n++) ceiling = Release(ceiling);
                    state.Ceiling = ceiling;
                    state.Released = releases;
                    if (ceiling == ResourceAction.None) state.RestoreReason ??= state.SlowSince is null ? "starvation-stall-restore-required" : "starvation-low-rate-restore-required";
                }
                status = state.Ceiling is null ? "starvation-continue-progress-monitoring" : state.SlowSince is null ? "starvation-stall-release" : "starvation-low-rate-release";
            } else status = "starvation-continue-progress-unknown-bounded";
            if (state.RestoreReason is not null) {
                action = ResourceAction.None;
                status = state.RestoreReason;
            } else if (state.Ceiling is ResourceAction ceiling) action &= ceiling & input.CurrentAction;
        }
        state.Expected = action != ResourceAction.None && action != input.CurrentAction ? action : ResourceAction.None;
        double stalled = state.Started is not null && state.HasProgress ? Math.Max(0, (state.Touched - state.LastProgress).TotalSeconds) : 0;
        double recovery = state.RecoveryUntil is DateTimeOffset recoveryUntil ? Math.Max(0, (recoveryUntil - now).TotalSeconds) : 0;
        return decision with { Action = action, Reason = decision.Reason + ";" + status + FormattableString.Invariant(
            $";restricted-seconds={restricted:0.###};stalled-seconds={stalled:0.###};progress-known={state.HasProgress};recovery-seconds={recovery:0.###}") };
    }

    static ResourceAction Release(ResourceAction action) {
        foreach (var control in new[] { ResourceAction.EcoQos, ResourceAction.LowerMemory, ResourceAction.CpuSets, ResourceAction.LowerCpu })
            if (action.HasFlag(control)) return action & ~control;
        return ResourceAction.None;
    }
}
